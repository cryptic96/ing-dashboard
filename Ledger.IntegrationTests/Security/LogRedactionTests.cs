using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.IntegrationTests.Ingestion;
using Ledger.Repository;
using Ledger.Repository.Stores;
using Ledger.Service.Ingestion.EnableBanking;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Security;

/// <summary>Proves secrets sent as headers, query strings, paths or configuration values never reach logs, responses or metrics.</summary>
[Collection("Database")]
public class LogRedactionTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Sentinel_in_header_query_and_path_never_reaches_logs_responses_or_metrics()
    {
        var sentinel = $"LedgerSentinel{Guid.NewGuid():N}";
        var urlEncodedSentinel = Uri.EscapeDataString(sentinel);
        var base64Sentinel = Convert.ToBase64String(Encoding.UTF8.GetBytes(sentinel));
        var base64UrlSafeSentinel = base64Sentinel.Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var (_, validToken, _) = await CreateActiveKeyAsync("log-redaction-test");

        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await WaitUntilReadyAsync(factory);

        var responseBodies = new List<string>();

        using (var headerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status"))
        {
            headerRequest.Headers.Add("X-Api-Key", sentinel);
            using var headerResponse = await client.SendAsync(headerRequest, TestContext.Current.CancellationToken);
            responseBodies.Add(await headerResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var queryRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/status?probe={urlEncodedSentinel}"))
        {
            queryRequest.Headers.Add("X-Api-Key", validToken);
            using var queryResponse = await client.SendAsync(queryRequest, TestContext.Current.CancellationToken);
            responseBodies.Add(await queryResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var pathRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/{base64UrlSafeSentinel}"))
        {
            pathRequest.Headers.Add("X-Api-Key", validToken);
            using var pathResponse = await client.SendAsync(pathRequest, TestContext.Current.CancellationToken);
            responseBodies.Add(await pathResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using var opsClient = factory.CreateOpsClient();
        using var metricsResponse = await opsClient.GetAsync("/metrics", TestContext.Current.CancellationToken);
        var metricsBody = await metricsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var logText = string.Join('\n', factory.CapturedLogMessages);

        foreach (var marker in new[] { sentinel, urlEncodedSentinel, base64Sentinel, base64UrlSafeSentinel })
        {
            logText.Should().NotContain(marker);
            metricsBody.Should().NotContain(marker);
            responseBodies.Should().OnlyContain(body => !body.Contains(marker, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Sentinel_connection_string_password_never_reaches_logs_or_the_health_response()
    {
        var sentinel = $"LedgerSentinelDbPass{Guid.NewGuid():N}";
        var badConnectionString = $"Host=/nonexistent;Database=ledger;Username=ledger_runtime;Password={sentinel}";

        await using var factory = new LedgerWebApplicationFactory(badConnectionString);
        using var opsClient = factory.CreateOpsClient();

        using var response = await WaitForStatusAsync(opsClient, HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().NotContain(sentinel);

        var logText = string.Join('\n', factory.CapturedLogMessages);
        logText.Should().NotContain(sentinel);
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public void Sentinel_certificate_password_never_reaches_the_startup_exception_chain()
    {
        var sentinel = $"LedgerSentinelCertPass{Guid.NewGuid():N}";

        var act = () => new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            certificatePath: "/nonexistent/ledger-sentinel-cert.pfx",
            certificatePassword: sentinel);

        var exception = act.Should().Throw<Exception>().Which;
        var messages = AllMessages(exception).ToList();

        messages.Should().NotContain(message => message.Contains(sentinel));
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Unhandled_exception_returns_problem_json_without_sentinel_stack_trace_or_exception_type()
    {
        var sentinel = $"LedgerSentinelException{Guid.NewGuid():N}";
        var (_, token, _) = await CreateActiveKeyAsync("throwing-endpoint-test");

        await using var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            configureTestServices: services =>
                services.AddSingleton<IStartupFilter>(new ThrowingEndpointStartupFilter(sentinel)));
        using var client = factory.CreateApiClient();
        await WaitUntilReadyAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/__test-throw");
        request.Headers.Add("X-Api-Key", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(sentinel);
        body.Should().NotContain("StackTrace");
        body.Should().NotContain(nameof(InvalidOperationException));
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Empty_api_key_header_returns_401_not_500_and_is_never_logged()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await WaitUntilReadyAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        request.Headers.Add("X-Api-Key", "");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var logText = string.Join('\n', factory.CapturedLogMessages);
        logText.Should().NotContain("X-Api-Key: ");
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Resolved_db_context_options_report_sensitive_data_logging_disabled()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var opsClient = factory.CreateOpsClient();
        using var readyResponse = await WaitForStatusAsync(opsClient, HttpStatusCode.OK);
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var options = factory.Services.GetRequiredService<DbContextOptions<LedgerDbContext>>();
        var coreOptionsExtension = options.FindExtension<CoreOptionsExtension>();

        coreOptionsExtension.Should().NotBeNull();
        coreOptionsExtension!.IsSensitiveDataLoggingEnabled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Link_state_code_and_session_id_never_reach_logs_responses_or_metrics_across_a_full_link_and_sync()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-redaction-001", -9.99m, new DateOnly(2026, 9, 30)));
        var wrongCode = $"LedgerSentinelWrongCode{Guid.NewGuid():N}";

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var responseBodies = new List<string>();

        string state;
        using (var link = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/connections/link"))
        {
            var linkBody = await link.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            state = BankLinkTestHost.StateFromAuthorizationUrl(
                System.Text.Json.JsonDocument.Parse(linkBody).RootElement.GetProperty("authorizationUrl").GetString()!);
        }

        using (var failed = await host.CallbackAsync(await host.StartLinkAsync(), wrongCode))
        {
            responseBodies.Add(await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var callback = await host.CallbackAsync(state, scenario.AuthorizationCode))
        {
            callback.StatusCode.Should().Be(HttpStatusCode.OK);
            responseBodies.Add(await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var connectionKey = await host.LatestConnectionKeyAsync();
        var accounts = await host.ListAccountsAsync(connectionKey);
        responseBodies.Add(System.Text.Json.JsonSerializer.Serialize(accounts));

        var selection = await host.SelectAsync(connectionKey, (accounts[0].GetProperty("accountKey").GetString()!, "Redaction", true));
        responseBodies.Add(selection.ToString());

        await host.WaitForReportedRowsAsync(accounts[0].GetProperty("accountKey").GetString()!, expected: 1);

        using var opsClient = host.Factory.CreateOpsClient();
        using var metricsResponse = await opsClient.GetAsync("/metrics", TestContext.Current.CancellationToken);
        var metricsBody = await metricsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var logText = string.Join('\n', host.Factory.CapturedLogMessages);

        foreach (var sentinel in new[] { state, scenario.AuthorizationCode, scenario.SessionId, wrongCode })
        {
            logText.Should().NotContain(sentinel);
            metricsBody.Should().NotContain(sentinel);
            responseBodies.Should().OnlyContain(body => !body.Contains(sentinel, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "LogRedaction")]
    public async Task Client_token_key_material_session_id_and_code_never_reach_logs_responses_or_metrics_through_the_adapter()
    {
        var keyPassword = $"LedgerSentinelKeyPass{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "ledger-redaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var keyPath = Path.Combine(directory, "key.pem");
            using (var rsa = RSA.Create(2048))
            {
                await File.WriteAllTextAsync(
                    keyPath,
                    rsa.ExportEncryptedPkcs8PrivateKeyPem(keyPassword, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)),
                    TestContext.Current.CancellationToken);
            }

            var firstKeyBodyLine = File.ReadAllLines(keyPath)[1];

            var scenario = SyntheticBankScenario.Create();
            var account = scenario.AddAccount(AccountKind.Current);
            scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-redaction-adapter-001", -9.99m, new DateOnly(2026, 9, 30)));
            var wrongCode = $"LedgerSentinelWrongCode{Guid.NewGuid():N}";
            var fake = new FakeEnableBankingHandler(scenario, BankLinkTestHost.RedirectUrl);

            await using var host = await StartAdapterHostAsync(fake, keyPath, keyPassword);

            var responseBodies = new List<string>();

            var state = await host.StartLinkAsync();

            using (var failed = await host.CallbackAsync(await host.StartLinkAsync(), wrongCode))
            {
                responseBodies.Add(await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            using (var callback = await host.CallbackAsync(state, scenario.AuthorizationCode))
            {
                callback.StatusCode.Should().Be(HttpStatusCode.OK);
                responseBodies.Add(await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            var connectionKey = await host.LatestConnectionKeyAsync();
            var accounts = await host.ListAccountsAsync(connectionKey);
            responseBodies.Add(System.Text.Json.JsonSerializer.Serialize(accounts));

            var accountKey = accounts[0].GetProperty("accountKey").GetString()!;
            responseBodies.Add((await host.SelectAsync(connectionKey, (accountKey, "Redaction", true))).ToString());
            await host.WaitForReportedRowsAsync(accountKey, expected: 1);

            var connectionId = Guid.Parse((await host.ReadAsync($"SELECT id::text FROM public.bank_connections WHERE connection_key = '{connectionKey}'")).Single());
            fake.FailNext(HttpStatusCode.TooManyRequests, "ASPSP_RATE_LIMIT_EXCEEDED");
            (await IngestionTestSupport.SyncAsync(host.Factory, connectionId)).Outcome.Should().Be(SyncOutcome.FailedRateLimited);
            (await IngestionTestSupport.SyncAsync(host.Factory, connectionId)).Outcome.Should().Be(SyncOutcome.Succeeded);

            var authorizationValues = fake.Requests
                .Select(request => request.Headers.GetValueOrDefault("Authorization"))
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)
                .ToList();
            authorizationValues.Should().NotBeEmpty();

            using var opsClient = host.Factory.CreateOpsClient();
            using var metricsResponse = await opsClient.GetAsync("/metrics", TestContext.Current.CancellationToken);
            var metricsBody = await metricsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var logText = string.Join('\n', host.Factory.CapturedLogMessages);

            var sentinels = authorizationValues
                .SelectMany(value => new[] { value, value.Replace("Bearer ", string.Empty, StringComparison.Ordinal) })
                .Concat([keyPassword, firstKeyBodyLine, scenario.SessionId, scenario.AuthorizationCode, wrongCode])
                .Distinct()
                .ToList();

            foreach (var sentinel in sentinels)
            {
                logText.Should().NotContain(sentinel);
                metricsBody.Should().NotContain(sentinel);
                responseBodies.Should().OnlyContain(body => !body.Contains(sentinel, StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<BankLinkTestHost> StartAdapterHostAsync(FakeEnableBankingHandler fake, string keyPath, string keyPassword)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["BankLink:RedirectUrl"] = BankLinkTestHost.RedirectUrl,
            ["EnableBanking:ApplicationId"] = "00000000-0000-0000-0000-000000000001",
            ["EnableBanking:PrivateKeyPath"] = keyPath,
            ["EnableBanking:PrivateKeyPassword"] = keyPassword,
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

    private static IEnumerable<string> AllMessages(Exception? exception)
    {
        while (exception is not null)
        {
            yield return exception.Message;
            exception = exception.InnerException;
        }
    }

    private async Task<(string Name, string Token, string KeyId)> CreateActiveKeyAsync(string name)
    {
        await using var context = CreateRuntimeContext();
        var store = new ApiKeyStore(context);
        var created = await store.CreateAsync(name, TestContext.Current.CancellationToken);
        return (created.Name, created.Token, created.KeyId);
    }

    private LedgerDbContext CreateRuntimeContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(fixture.ConnectionStringFor("ledger_runtime"));
        return new LedgerDbContext(optionsBuilder.Options);
    }

    private static async Task WaitUntilReadyAsync(LedgerWebApplicationFactory factory)
    {
        using var opsClient = factory.CreateOpsClient();
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

    private static async Task<HttpResponseMessage> WaitForStatusAsync(
        HttpClient client,
        HttpStatusCode expectedStatus,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        HttpResponseMessage? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last?.Dispose();
                last = await client.GetAsync("/health", TestContext.Current.CancellationToken);

                if (last.StatusCode == expectedStatus)
                {
                    return last;
                }
            }
            catch (HttpRequestException)
            {
                last = null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        return last ?? throw new TimeoutException(
            $"The ops endpoint never reached status {expectedStatus} within the timeout.");
    }

    /// <summary>Test-only middleware adding an authenticated endpoint that always throws, proving unhandled exceptions never leak to callers.</summary>
    private sealed class ThrowingEndpointStartupFilter(string sentinel) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                next(app);

                app.Map("/api/v1/__test-throw", branch => branch.Run(_ =>
                    throw new InvalidOperationException($"boom-{sentinel}")));
            };
        }
    }
}
