using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.EnableBanking;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Runs the whole link and sync pipeline through the real aggregator client against a fake aggregator that speaks its API, so
/// the provider-agnostic pipeline is proven unchanged when the provider is the real one.
/// </summary>
[Collection("Database")]
[Trait("Category", "EnableBanking")]
public sealed class EnableBankingPipelineTests(DatabaseFixture fixture) : IDisposable
{
    private const string KeyPassword = "synthetic-key-password-for-tests";
    private static readonly DateOnly Day = new(2026, 9, 30);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ledger-eb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Link_callback_selection_and_sync_run_through_the_real_adapter_with_longest_history_and_every_continuation_page()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 2;
        var joint = scenario.AddAccount(AccountKind.Current);
        var other = scenario.AddAccount(AccountKind.Current);
        for (var index = 1; index <= 5; index++)
        {
            scenario.AddTransaction(joint, IngestionTestSupport.Booked($"entry-eb-{index}", -index, Day.AddDays(-index)));
        }

        scenario.AddTransaction(other, IngestionTestSupport.Booked("entry-eb-other", 10.00m, Day));

        var fake = new FakeEnableBankingHandler(scenario, BankLinkTestHost.RedirectUrl);
        await using var host = await StartAsync(fake);

        var state = await host.StartLinkAsync();
        using (var callback = await host.CallbackAsync(state, scenario.AuthorizationCode))
        {
            callback.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var connectionKey = await host.LatestConnectionKeyAsync();
        var accounts = await host.ListAccountsAsync(connectionKey);
        accounts.Should().HaveCount(2);
        var jointKey = accounts[0].GetProperty("accountKey").GetString()!;
        var otherKey = accounts[1].GetProperty("accountKey").GetString()!;

        var selection = await host.SelectAsync(connectionKey, (jointKey, "Joint", true), (otherKey, "Other", false));
        selection.GetProperty("firstSync").GetString().Should().Be("queued");

        var reported = await host.WaitForReportedRowsAsync(jointKey, expected: 5);
        reported.Should().HaveCount(5);
        (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, otherKey)).Should().BeEmpty();

        var firstFetch = fake.Requests.Where(IsTransactionRequest).ToList();
        firstFetch.Should().HaveCount(3);
        firstFetch[0].Parameter("strategy").Should().Be("longest");
        firstFetch[0].Parameter("date_from").Should().BeNull();
        firstFetch[0].Parameter("continuation_key").Should().BeNull();
        firstFetch.Skip(1).Select(request => request.Parameter("continuation_key")).Should().Equal("page-1", "page-2");
        firstFetch.Select(request => request.Parameter("strategy")).Should().OnlyContain(value => value == "longest");
        fake.Requests.Should().NotContain(request => request.Path.Contains(other.Uid, StringComparison.Ordinal));

        var connectionId = Guid.Parse((await host.ReadAsync($"SELECT id::text FROM public.bank_connections WHERE connection_key = '{connectionKey}'")).Single());
        var before = await IngestionTestSupport.ReadCountsAsync(fixture, jointKey);
        var second = await IngestionTestSupport.SyncAsync(host.Factory, connectionId);

        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Inserted.Should().Be(0);
        (await IngestionTestSupport.ReadCountsAsync(fixture, jointKey)).Should().Be(before);
        fake.Requests.Where(IsTransactionRequest).Skip(3).Should().OnlyContain(request => request.Parameter("date_from") != null);

        (await IngestionTestSupport.CountIdentityViolationsAsync(fixture)).Should().Be(0);
        (await host.ReadAsync($"SELECT provider FROM public.accounts WHERE account_key = '{jointKey}'")).Single().Should().Be("enablebanking");

        fake.Requests.Select(request => request.Method + " " + Generalise(request.Path)).Distinct().Should().BeSubsetOf(
        [
            "GET /application",
            "GET /aspsps",
            "POST /auth",
            "POST /sessions",
            "GET /accounts/{id}/balances",
            "GET /accounts/{id}/transactions"
        ]);
    }

    [Fact]
    public async Task The_authorisation_address_must_be_https_on_an_allowed_host_or_the_link_is_refused()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var fake = new FakeEnableBankingHandler(scenario, BankLinkTestHost.RedirectUrl) { AuthorizationHost = "auth.example.org" };
        await using var host = await StartAsync(fake);

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/connections/link");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain("example.org");
    }

    [Fact]
    public async Task A_rate_limit_answer_ends_the_sync_as_rate_limited_and_stores_only_the_error_code()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-eb-rate", -1.00m, Day));
        var fake = new FakeEnableBankingHandler(scenario, BankLinkTestHost.RedirectUrl);
        await using var host = await StartAsync(fake);

        var state = await host.StartLinkAsync();
        using (var callback = await host.CallbackAsync(state, scenario.AuthorizationCode))
        {
            callback.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var connectionKey = await host.LatestConnectionKeyAsync();
        var accountKey = (await host.ListAccountsAsync(connectionKey))[0].GetProperty("accountKey").GetString()!;
        await host.SelectAsync(connectionKey, (accountKey, "Joint", true));
        await host.WaitForReportedRowsAsync(accountKey, expected: 1);

        var connectionId = Guid.Parse((await host.ReadAsync($"SELECT id::text FROM public.bank_connections WHERE connection_key = '{connectionKey}'")).Single());
        fake.FailNext(HttpStatusCode.TooManyRequests, "ASPSP_RATE_LIMIT_EXCEEDED");

        var result = await IngestionTestSupport.SyncAsync(host.Factory, connectionId);

        result.Outcome.Should().Be(SyncOutcome.FailedRateLimited);
        var run = await IngestionTestSupport.ReadRunAsync(fixture, result.RunId);
        run.ProviderError.Should().Be("ASPSP_RATE_LIMIT_EXCEEDED");
    }

    private static bool IsTransactionRequest(RecordedAggregatorRequest request)
    {
        return request.Method == "GET" && request.Path.EndsWith("/transactions", StringComparison.Ordinal);
    }

    private static string Generalise(string path)
    {
        var segments = path.Split('/');
        return segments.Length == 4 && segments[1] == "accounts" ? $"/accounts/{{id}}/{segments[3]}" : path;
    }

    private async Task<BankLinkTestHost> StartAsync(FakeEnableBankingHandler fake)
    {
        Directory.CreateDirectory(_directory);
        var keyPath = Path.Combine(_directory, "key.pem");

        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(
                keyPath,
                rsa.ExportEncryptedPkcs8PrivateKeyPem(KeyPassword, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)),
                TestContext.Current.CancellationToken);
        }

        var configuration = new Dictionary<string, string?>
        {
            ["BankLink:RedirectUrl"] = BankLinkTestHost.RedirectUrl,
            ["EnableBanking:ApplicationId"] = "00000000-0000-0000-0000-000000000001",
            ["EnableBanking:PrivateKeyPath"] = keyPath,
            ["EnableBanking:PrivateKeyPassword"] = KeyPassword,
            ["Ingestion:BackgroundCallsPerDay"] = "1000"
        };

        const string providerVariable = "Ingestion__Provider";
        Environment.SetEnvironmentVariable(providerVariable, "EnableBanking");

        LedgerWebApplicationFactory factory;

        try
        {
            factory = new LedgerWebApplicationFactory(
                fixture.ConnectionStringFor("ledger_runtime"),
                configureTestServices: services =>
                    services.AddHttpClient<EnableBankingClient>().ConfigurePrimaryHttpMessageHandler(() => fake),
                additionalConfiguration: configuration);
        }
        finally
        {
            Environment.SetEnvironmentVariable(providerVariable, null);
        }

        return await BankLinkTestHost.StartWithFactoryAsync(fixture, factory);
    }
}
