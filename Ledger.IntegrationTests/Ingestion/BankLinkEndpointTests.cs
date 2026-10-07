using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository;
using Ledger.Repository.Stores;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>Drives the guided bank link over real HTTP against the synthetic provider and real PostgreSQL.</summary>
[Collection("Database")]
[Trait("Category", "Callback")]
public class BankLinkEndpointTests(DatabaseFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 9, 30);

    [Fact]
    public async Task Link_callback_selection_and_first_sync_work_end_to_end_over_http()
    {
        var scenario = SyntheticBankScenario.Create();
        var joint = scenario.AddAccount(AccountKind.Current);
        var savings = scenario.AddAccount(AccountKind.Savings);
        scenario.AddTransaction(joint, IngestionTestSupport.Booked("entry-001", -12.34m, Day));
        scenario.AddTransaction(joint, IngestionTestSupport.Booked("entry-002", 1500.00m, Day.AddDays(-1), "Example Employer", "Salary"));
        scenario.AddTransaction(savings, IngestionTestSupport.Booked("entry-901", 25.00m, Day));

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var state = await host.StartLinkAsync();
        using var callback = await host.CallbackAsync(state, scenario.AuthorizationCode);

        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        var callbackBody = await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        callbackBody.Should().Contain("2 accounts");

        var connectionKey = await host.LatestConnectionKeyAsync();
        var accounts = await host.ListAccountsAsync(connectionKey);

        accounts.Should().HaveCount(2);
        accounts.Select(account => account.GetProperty("syncEnabled").GetBoolean()).Should().AllBeEquivalentTo(false);
        accounts.Select(account => account.GetProperty("maskedIban").GetString()).Should().AllSatisfy(masked =>
        {
            masked.Should().StartWith("XX").And.Contain("…").And.HaveLength(7);
        });

        var jointKey = accounts[0].GetProperty("accountKey").GetString()!;
        var selection = await host.SelectAsync(connectionKey, (jointKey, "Joint", true), (accounts[1].GetProperty("accountKey").GetString()!, "Spaar", false));

        selection.GetProperty("firstSync").GetString().Should().Be("queued");

        var reported = await host.WaitForReportedRowsAsync(jointKey, expected: 2);
        reported.Should().OnlyContain(row => row == "Joint");

        var transactionCalls = scenario.Calls.Where(call => call.Method == nameof(IBankDataProvider.GetTransactionsAsync)).ToList();
        transactionCalls.Should().ContainSingle();
        transactionCalls[0].AccountUid.Should().Be(joint.Uid);
        transactionCalls[0].Query.Should().Contain("depth=Longest");
        scenario.Calls.Where(call => call.AccountUid == savings.Uid).Should().BeEmpty();
    }

    [Fact]
    public async Task Stored_session_id_is_protected_and_no_plaintext_state_is_stored()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var state = await host.StartLinkAsync();
        using var callback = await host.CallbackAsync(state, scenario.AuthorizationCode);
        callback.StatusCode.Should().Be(HttpStatusCode.OK);

        var protectedValues = await host.ReadAsync("SELECT session_id_protected FROM public.bank_connections");
        protectedValues.Should().NotBeEmpty();
        protectedValues.Should().NotContain(value => value == scenario.SessionId);
        protectedValues.Should().NotContain(value => value.Contains(scenario.SessionId, StringComparison.Ordinal));

        var stateHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        var hashes = await host.ReadAsync("SELECT encode(state_sha256, 'hex') FROM public.bank_authorizations");
        hashes.Should().Contain(stateHash);

        var rows = await host.ReadAsync("SELECT t::text FROM public.bank_authorizations t");
        rows.Should().NotContain(row => row.Contains(state, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_callback_failure_returns_the_same_status_and_body_with_no_store_and_no_referrer_headers()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var reusedState = await host.StartLinkAsync();
        using (var first = await host.CallbackAsync(reusedState, scenario.AuthorizationCode))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertHardenedHeaders(first);
        }

        var wrongCodeState = await host.StartLinkAsync();
        var errorState = await host.StartLinkAsync();
        var expiredState = await host.StoreExpiredStateAsync();
        var unknownState = LinkStateToken.Generate().State;

        var failures = new List<(string Name, HttpStatusCode Status, string Body)>
        {
            await Capture("reused", host.CallbackAsync(reusedState, scenario.AuthorizationCode)),
            await Capture("unknown", host.CallbackAsync(unknownState, scenario.AuthorizationCode)),
            await Capture("malformed", host.CallbackAsync("not-a-state", scenario.AuthorizationCode)),
            await Capture("expired", host.CallbackAsync(expiredState, scenario.AuthorizationCode)),
            await Capture("wrong code", host.CallbackAsync(wrongCodeState, "a-code-the-bank-never-issued")),
            await Capture(
                "cancelled",
                host.SendAsync(HttpMethod.Get, $"/api/v1/bank/callback?state={errorState}&error=access_denied", withKey: false)),
            await Capture("missing parameters", host.SendAsync(HttpMethod.Get, "/api/v1/bank/callback", withKey: false)),
            await Capture(
                "oversized code",
                host.CallbackAsync(LinkStateToken.Generate().State, new string('c', 2049)))
        };

        failures.Select(failure => failure.Status).Distinct().Should().ContainSingle().Which.Should().Be(HttpStatusCode.BadRequest);
        failures.Select(failure => failure.Body).Distinct().Should().ContainSingle();
        failures[0].Body.Should().NotContain(reusedState).And.NotContain(scenario.AuthorizationCode);

        using var afterCancel = await host.CallbackAsync(errorState, scenario.AuthorizationCode);
        afterCancel.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        static async Task<(string, HttpStatusCode, string)> Capture(string name, Task<HttpResponseMessage> pending)
        {
            using var response = await pending;
            AssertHardenedHeaders(response);
            return (name, response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_session_without_accounts_gets_a_distinct_safe_message_about_the_control_panel()
    {
        var scenario = SyntheticBankScenario.Create();

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var state = await host.StartLinkAsync();
        using var response = await host.CallbackAsync(state, scenario.AuthorizationCode);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertHardenedHeaders(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("control panel").And.NotContain(state);
    }

    [Fact]
    public async Task Only_the_bank_callback_endpoint_allows_anonymous_access()
    {
        var scenario = SyntheticBankScenario.Create();

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var endpoints = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var anonymous = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null).ToList();
        anonymous.Should().ContainSingle().Which.RoutePattern.RawText.Should().Be("/api/v1/bank/callback");

        var bankRoutes = endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/bank", StringComparison.Ordinal))
            .Where(endpoint => endpoint.RoutePattern.RawText != "/api/v1/bank/callback")
            .ToList();
        bankRoutes.Should().HaveCountGreaterThanOrEqualTo(4);

        foreach (var endpoint in bankRoutes)
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.First();
            var path = Regex.Replace(endpoint.RoutePattern.RawText!, @"\{[^}]+\}", "placeholder");

            using var response = await host.SendAsync(new HttpMethod(method), path, withKey: false);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} must need a key");
        }
    }

    [Fact]
    public async Task Without_a_configured_provider_starting_a_link_answers_that_bank_linking_is_not_configured()
    {
        await using var host = await BankLinkTestHost.StartWithoutProviderAsync(fixture);

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/connections/link");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("not configured");
    }

    [Fact]
    public async Task The_synthetic_provider_selected_by_configuration_links_a_demo_bank()
    {
        await using var host = await BankLinkTestHost.StartWithProviderSettingAsync(fixture, "Synthetic");

        var state = await host.StartLinkAsync();
        using var response = await host.CallbackAsync(state, "any-code-works-for-the-demo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("2 accounts");
    }

    [Theory]
    [InlineData("NotAProviderSentinel")]
    public void An_unusable_provider_value_stops_startup_naming_only_the_key(string provider)
    {
        var act = () => BankLinkTestHost.CreateFactoryWithProviderSetting(fixture, provider);

        var thrown = act.Should().Throw<Exception>().Which;
        var messages = new List<string>();
        for (Exception? exception = thrown; exception is not null; exception = exception.InnerException)
        {
            messages.Add(exception.Message);
        }

        messages.Should().Contain(message => message.Contains("Ingestion:Provider"));
        messages.Should().NotContain(message => message.Contains("NotAProviderSentinel"));
    }

    [Fact]
    [Trait("Category", "Consent")]
    public async Task Connections_show_the_consent_state_and_whole_days_left_and_never_a_session_id()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        scenario.SessionValidUntil = DateTimeOffset.UtcNow.AddDays(10).AddHours(1);

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var expiringKey = await host.LinkAsync(scenario);
        var expiring = (await host.ListConnectionsAsync()).Single(connection => connection.GetProperty("connectionKey").GetString() == expiringKey);

        expiring.GetProperty("consentState").GetString().Should().Be("expiring");
        expiring.GetProperty("daysUntilExpiry").GetInt32().Should().Be(10);
        expiring.GetProperty("status").GetString().Should().Be("active");

        scenario.SessionValidUntil = DateTimeOffset.UtcNow.AddDays(90);
        var linkedKey = await host.LinkAsync(scenario);

        var connections = await host.ListConnectionsAsync();
        var linked = connections.Single(connection => connection.GetProperty("connectionKey").GetString() == linkedKey);

        linked.GetProperty("consentState").GetString().Should().Be("linked");
        linked.GetProperty("daysUntilExpiry").GetInt32().Should().BeInRange(89, 90);
        linked.GetProperty("status").GetString().Should().Be("active");

        var raw = System.Text.Json.JsonSerializer.Serialize(connections);
        raw.Should().NotContain(scenario.SessionId).And.NotContainEquivalentOf("session");
    }

    [Fact]
    [Trait("Category", "Consent")]
    public async Task Renewal_keeps_account_keys_names_selection_and_history_and_syncs_the_longest_history_without_duplicates()
    {
        var scenario = SyntheticBankScenario.Create();
        var first = scenario.AddAccount(AccountKind.Current);
        var second = scenario.AddAccount(AccountKind.Savings);
        scenario.AddTransaction(first, IngestionTestSupport.Booked("entry-a-1", -10.00m, Day));
        scenario.AddTransaction(first, IngestionTestSupport.Booked("entry-a-2", -20.00m, Day.AddDays(-1)));
        scenario.AddTransaction(second, IngestionTestSupport.Booked("entry-b-1", 5.00m, Day));
        var bankProvider = new RenewableSyntheticProvider(scenario);

        await using var host = await BankLinkTestHost.StartAsync(fixture, bankProvider);

        var originalKey = await host.LinkAsync(scenario);
        var original = await host.ListAccountsAsync(originalKey);
        var firstKey = original[0].GetProperty("accountKey").GetString()!;
        var secondKey = original[1].GetProperty("accountKey").GetString()!;

        await host.SelectAsync(originalKey, (firstKey, "Joint", true), (secondKey, "Spaar", true));
        (await host.WaitForReportedRowsAsync(firstKey, expected: 2)).Should().HaveCount(2);
        (await host.WaitForReportedRowsAsync(secondKey, expected: 1)).Should().HaveCount(1);
        var idsBefore = (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, firstKey)).Select(row => row.TransactionId).ToList();

        scenario.AddTransaction(first, IngestionTestSupport.Booked("entry-a-3", -30.00m, Day.AddDays(1)));
        bankProvider.ExposeRenewedSession = true;

        var renewState = await host.StartRenewAsync(originalKey);
        using (var callback = await host.CallbackAsync(renewState, scenario.AuthorizationCode))
        {
            callback.StatusCode.Should().Be(HttpStatusCode.OK);
            (await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("2 accounts");
        }

        var renewedKey = await host.LatestConnectionKeyAsync();
        renewedKey.Should().NotBe(originalKey);

        var renewed = await host.ListAccountsAsync(renewedKey);
        renewed.Should().HaveCount(2);
        var kept = renewed.Single(account => account.GetProperty("accountKey").GetString() == firstKey);
        kept.GetProperty("displayName").GetString().Should().Be("Joint");
        kept.GetProperty("syncEnabled").GetBoolean().Should().BeTrue();
        var added = renewed.Single(account => account.GetProperty("accountKey").GetString() != firstKey);
        added.GetProperty("accountKey").GetString().Should().NotBe(secondKey);
        added.GetProperty("syncEnabled").GetBoolean().Should().BeFalse();
        added.GetProperty("displayName").ValueKind.Should().Be(JsonValueKind.Null);

        var connections = await host.ListConnectionsAsync();
        var supersededEntry = connections.Single(connection => connection.GetProperty("connectionKey").GetString() == originalKey);
        supersededEntry.GetProperty("status").GetString().Should().Be("superseded");
        supersededEntry.GetProperty("consentState").GetString().Should().Be("superseded");
        connections.Single(connection => connection.GetProperty("connectionKey").GetString() == renewedKey)
            .GetProperty("status").GetString().Should().Be("active");

        var afterRows = await host.WaitForReportedRowsAsync(firstKey, expected: 3);
        afterRows.Should().HaveCount(3);
        var idsAfter = (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, firstKey)).Select(row => row.TransactionId).ToList();
        idsAfter.Should().Contain(idsBefore);
        idsAfter.Should().OnlyHaveUniqueItems();
        (await IngestionTestSupport.ReadCountsAsync(fixture, firstKey)).Should().Be(new RowCounts(3, 3, 3));
        (await host.WaitForReportedRowsAsync(secondKey, expected: 1)).Should().HaveCount(1);

        var longestFetches = scenario.Calls.Where(call =>
            call.Method == nameof(IBankDataProvider.GetTransactionsAsync)
            && call.AccountUid == first.Uid
            && call.Query.Contains("depth=Longest", StringComparison.Ordinal));
        longestFetches.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Consent")]
    public async Task A_renew_state_whose_connection_was_superseded_meanwhile_gets_the_generic_failure()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var bankProvider = new RenewableSyntheticProvider(scenario) { ExposeRenewedSession = true };

        await using var host = await BankLinkTestHost.StartAsync(fixture, bankProvider);

        var connectionKey = await host.LinkAsync(scenario);
        var firstRenewal = await host.StartRenewAsync(connectionKey);
        var secondRenewal = await host.StartRenewAsync(connectionKey);

        using var accepted = await host.CallbackAsync(firstRenewal, scenario.AuthorizationCode);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var rejected = await host.CallbackAsync(secondRenewal, scenario.AuthorizationCode);
        using var unknown = await host.CallbackAsync(LinkStateToken.Generate().State, scenario.AuthorizationCode);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Consent")]
    public async Task Revoking_ends_the_provider_session_marks_the_connection_revoked_and_blocks_renewal()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var bankProvider = new RenewableSyntheticProvider(scenario);

        await using var host = await BankLinkTestHost.StartAsync(fixture, bankProvider);

        var connectionKey = await host.LinkAsync(scenario);

        using (var revoke = await host.SendAsync(HttpMethod.Delete, $"/api/v1/bank/connections/{connectionKey}"))
        {
            revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        bankProvider.RevokedSessionIds.Should().Equal(scenario.SessionId);

        var entry = (await host.ListConnectionsAsync()).Single(connection => connection.GetProperty("connectionKey").GetString() == connectionKey);
        entry.GetProperty("status").GetString().Should().Be("revoked");
        entry.GetProperty("consentState").GetString().Should().Be("revoked");

        using var renew = await host.SendAsync(HttpMethod.Post, $"/api/v1/bank/connections/{connectionKey}/renew");
        renew.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var again = await host.SendAsync(HttpMethod.Delete, $"/api/v1/bank/connections/{connectionKey}");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var unknown = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/connections/0000000000000000/renew");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Consent")]
    public async Task A_provider_failure_while_revoking_returns_502_and_changes_nothing()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var bankProvider = new RenewableSyntheticProvider(scenario) { FailRevoke = true };

        await using var host = await BankLinkTestHost.StartAsync(fixture, bankProvider);

        var connectionKey = await host.LinkAsync(scenario);

        using var revoke = await host.SendAsync(HttpMethod.Delete, $"/api/v1/bank/connections/{connectionKey}");

        revoke.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var entry = (await host.ListConnectionsAsync()).Single(connection => connection.GetProperty("connectionKey").GetString() == connectionKey);
        entry.GetProperty("status").GetString().Should().Be("active");
    }

    private static void AssertHardenedHeaders(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
    }
}

/// <summary>A running host wired to a synthetic provider, with helpers that speak the bank link API the way an operator client does.</summary>
public sealed class BankLinkTestHost : IAsyncDisposable
{
    /// <summary>The https address the synthetic consent redirects to in tests.</summary>
    public const string RedirectUrl = "https://ledger-api.example.com/api/v1/bank/callback";

    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    private BankLinkTestHost(DatabaseFixture fixture, LedgerWebApplicationFactory factory, string token)
    {
        _fixture = fixture;
        Factory = factory;
        Token = token;
        _client = factory.CreateApiClient();
    }

    /// <summary>The running host.</summary>
    public LedgerWebApplicationFactory Factory { get; }

    /// <summary>A valid API key for the host's database.</summary>
    public string Token { get; }

    /// <summary>Boots a host with the scenario's provider registered and a fresh API key.</summary>
    public static Task<BankLinkTestHost> StartAsync(
        DatabaseFixture fixture,
        SyntheticBankScenario scenario,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        return StartAsync(fixture, new SyntheticBankDataProvider(scenario), additionalConfiguration);
    }

    /// <summary>Boots a host with the given provider registered and a fresh API key.</summary>
    public static async Task<BankLinkTestHost> StartAsync(
        DatabaseFixture fixture,
        IBankDataProvider bankProvider,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var configuration = new Dictionary<string, string?> { ["BankLink:RedirectUrl"] = RedirectUrl };

        if (additionalConfiguration is not null)
        {
            foreach (var pair in additionalConfiguration)
            {
                configuration[pair.Key] = pair.Value;
            }
        }

        var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            configureTestServices: services => services.AddSingleton(bankProvider),
            additionalConfiguration: configuration);

        var token = await CreateKeyAsync(fixture);
        var host = new BankLinkTestHost(fixture, factory, token);
        await host.WaitUntilReadyAsync();
        return host;
    }

    /// <summary>Wraps a host the caller has already built, creating a fresh API key for it.</summary>
    public static async Task<BankLinkTestHost> StartWithFactoryAsync(DatabaseFixture fixture, LedgerWebApplicationFactory factory)
    {
        var token = await CreateKeyAsync(fixture);
        var host = new BankLinkTestHost(fixture, factory, token);
        await host.WaitUntilReadyAsync();
        return host;
    }

    /// <summary>Boots a host with only configuration overrides and no injected provider.</summary>
    public static async Task<BankLinkTestHost> StartWithoutProviderAsync(
        DatabaseFixture fixture,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            additionalConfiguration: additionalConfiguration);

        var token = await CreateKeyAsync(fixture);
        var host = new BankLinkTestHost(fixture, factory, token);
        await host.WaitUntilReadyAsync();
        return host;
    }

    /// <summary>
    /// Boots a host whose provider is chosen by the Ingestion:Provider setting. The setting is read while services are
    /// registered, before the factory's configuration overrides apply, so it is passed as an environment value for the
    /// duration of startup only.
    /// </summary>
    public static async Task<BankLinkTestHost> StartWithProviderSettingAsync(DatabaseFixture fixture, string provider)
    {
        var factory = CreateFactoryWithProviderSetting(fixture, provider);
        var token = await CreateKeyAsync(fixture);
        var host = new BankLinkTestHost(fixture, factory, token);
        await host.WaitUntilReadyAsync();
        return host;
    }

    /// <summary>Creates the factory with Ingestion:Provider set as an environment value only while the host starts.</summary>
    public static LedgerWebApplicationFactory CreateFactoryWithProviderSetting(DatabaseFixture fixture, string provider)
    {
        const string name = "Ingestion__Provider";
        Environment.SetEnvironmentVariable(name, provider);

        try
        {
            return new LedgerWebApplicationFactory(
                fixture.ConnectionStringFor("ledger_runtime"),
                additionalConfiguration: new Dictionary<string, string?> { ["BankLink:RedirectUrl"] = RedirectUrl });
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Sends a request to the API port, with the host's key unless told otherwise.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool withKey = true)
    {
        using var request = new HttpRequestMessage(method, path);

        if (withKey)
        {
            request.Headers.Add("X-Api-Key", Token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a link and returns the one-time state carried by the authorisation address.</summary>
    public async Task<string> StartLinkAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/bank/connections/link");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return StateFromAuthorizationUrl(body.GetProperty("authorizationUrl").GetString()!);
    }

    /// <summary>Extracts the state parameter from an authorisation address.</summary>
    public static string StateFromAuthorizationUrl(string authorizationUrl)
    {
        var uri = new Uri(authorizationUrl);
        uri.Scheme.Should().Be("https");
        return HttpUtility.ParseQueryString(uri.Query)["state"]!;
    }

    /// <summary>Calls the anonymous callback the way the browser does after approval.</summary>
    public async Task<HttpResponseMessage> CallbackAsync(string state, string code)
    {
        return await SendAsync(
            HttpMethod.Get,
            $"/api/v1/bank/callback?state={Uri.EscapeDataString(state)}&code={Uri.EscapeDataString(code)}",
            withKey: false);
    }

    /// <summary>Stores a pending authorisation whose lifetime already ended and returns its state.</summary>
    public async Task<string> StoreExpiredStateAsync()
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(_fixture.ConnectionStringFor("ledger_runtime"));

        await using var context = new LedgerDbContext(optionsBuilder.Options);
        var store = new BankAuthorizationStore(context);
        var (state, sha256) = LinkStateToken.Generate();
        var now = DateTimeOffset.UtcNow;

        await store.CreateAsync(
            new PendingAuthorization(sha256, AuthorizationPurposes.Link, null, "expired-attempt", now.AddHours(-2), now.AddHours(-1)),
            TestContext.Current.CancellationToken);

        return state;
    }

    /// <summary>Returns the key of the most recently created connection.</summary>
    public async Task<string> LatestConnectionKeyAsync()
    {
        var keys = await ReadAsync("SELECT connection_key FROM public.bank_connections ORDER BY created_at DESC, id DESC LIMIT 1");
        return keys.Single();
    }

    /// <summary>Lists the accounts of a connection through the API.</summary>
    public async Task<IReadOnlyList<JsonElement>> ListAccountsAsync(string connectionKey)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/bank/connections/{connectionKey}/accounts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.EnumerateArray().ToList();
    }

    /// <summary>Starts a renewal of the connection and returns the one-time state carried by the authorisation address.</summary>
    public async Task<string> StartRenewAsync(string connectionKey)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/bank/connections/{connectionKey}/renew");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return StateFromAuthorizationUrl(body.GetProperty("authorizationUrl").GetString()!);
    }

    /// <summary>Links the scenario's consent and returns the new connection's key, without selecting any account.</summary>
    public async Task<string> LinkAsync(SyntheticBankScenario scenario)
    {
        var state = await StartLinkAsync();
        using var callback = await CallbackAsync(state, scenario.AuthorizationCode);
        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        return await LatestConnectionKeyAsync();
    }

    /// <summary>Lists every connection through the API.</summary>
    public async Task<IReadOnlyList<JsonElement>> ListConnectionsAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/bank/connections");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.EnumerateArray().ToList();
    }

    /// <summary>Saves an account selection and returns the response body.</summary>
    public async Task<JsonElement> SelectAsync(string connectionKey, params (string AccountKey, string DisplayName, bool Sync)[] selections)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"/api/v1/bank/connections/{connectionKey}/accounts",
            new
            {
                accounts = selections.Select(selection => new
                {
                    accountKey = selection.AccountKey,
                    displayName = selection.DisplayName,
                    sync = selection.Sync
                })
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the account names the Grafana reader sees for an account, waiting up to ten seconds for the expected row count.</summary>
    public async Task<IReadOnlyList<string>> WaitForReportedRowsAsync(string accountKey, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        IReadOnlyList<string> rows = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            rows = await ReadReportedAccountNamesAsync(accountKey);

            if (rows.Count >= expected)
            {
                return rows;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }

        return rows;
    }

    /// <summary>Runs a query that returns one text column as the backup role and returns every value.</summary>
    public async Task<IReadOnlyList<string>> ReadAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Factory.DisposeAsync();
    }

    private async Task<IReadOnlyList<string>> ReadReportedAccountNamesAsync(string accountKey)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionStringFor("grafana_reader"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_name FROM reporting.transactions WHERE account_key = @accountKey";
        command.Parameters.AddWithValue("accountKey", accountKey);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<string> CreateKeyAsync(DatabaseFixture fixture)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(fixture.ConnectionStringFor("ledger_runtime"));

        await using var context = new LedgerDbContext(optionsBuilder.Options);
        var store = new ApiKeyStore(context);
        var created = await store.CreateAsync("bl-" + Guid.NewGuid().ToString("N")[..12], TestContext.Current.CancellationToken);
        return created.Token;
    }

    private async Task WaitUntilReadyAsync()
    {
        using var opsClient = Factory.CreateOpsClient();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await opsClient.GetAsync("/health", TestContext.Current.CancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The ops endpoint never became healthy within the timeout.");
    }
}

/// <summary>
/// Wraps the synthetic provider so a renewal can return the same accounts under new provider ids, leave one account out and
/// expose one new account, and so a session revocation can be observed or made to fail.
/// </summary>
public sealed class RenewableSyntheticProvider(SyntheticBankScenario scenario) : IBankDataProvider
{
    private const string RenewedPrefix = "renewed-";
    private readonly SyntheticBankDataProvider _inner = new(scenario);
    private readonly List<string> _revokedSessionIds = [];

    /// <summary>The session id handed out for a renewed consent.</summary>
    public string RenewedSessionId { get; } = "renewed-session-" + Guid.NewGuid().ToString("N");

    /// <summary>When true, completing a consent returns the renewed session instead of the original one.</summary>
    public bool ExposeRenewedSession { get; set; }

    /// <summary>When true, revoking a session fails with a provider error.</summary>
    public bool FailRevoke { get; set; }

    /// <summary>The plaintext session ids the application asked the provider to end.</summary>
    public IReadOnlyList<string> RevokedSessionIds => _revokedSessionIds.ToList();

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        return _inner.StartAuthorizationAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken)
    {
        var session = await _inner.CompleteAuthorizationAsync(code, cancellationToken);

        if (!ExposeRenewedSession)
        {
            return session;
        }

        var kept = session.Accounts
            .Take(Math.Max(1, session.Accounts.Count - 1))
            .Select(account => account with { Uid = RenewedPrefix + account.Uid })
            .ToList();

        kept.Add(new ProviderAccount(
            RenewedPrefix + "new-" + Guid.NewGuid().ToString("N"),
            "renewed-new-" + Guid.NewGuid().ToString("N"),
            "XX00SYNT0000000099",
            "Synthetic new account",
            "Current",
            AccountKind.Current,
            "EUR"));

        return new ProviderSession(RenewedSessionId, session.ValidUntil, kept);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
        ProviderAccountRef account,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        return _inner.GetBalancesAsync(Original(account), context, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
        ProviderAccountRef account,
        TransactionQuery query,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        return _inner.GetTransactionsAsync(Original(account), query, context, cancellationToken);
    }

    /// <inheritdoc />
    public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (FailRevoke)
        {
            throw new BankProviderException(ProviderErrorKind.Transient, "revoke_failed", "The provider could not end the session.");
        }

        _revokedSessionIds.Add(sessionId);
        return _inner.RevokeSessionAsync(sessionId, cancellationToken);
    }

    private ProviderAccountRef Original(ProviderAccountRef account)
    {
        return account.SessionId == RenewedSessionId
            ? new ProviderAccountRef(scenario.SessionId, account.AccountUid[RenewedPrefix.Length..])
            : account;
    }
}
