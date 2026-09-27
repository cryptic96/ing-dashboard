using System.Net;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;

namespace Ledger.IntegrationTests.Skeleton;

/// <summary>Proves the skeleton host reads and writes the canary row as the runtime role and survives a restart and a redeploy.</summary>
[Collection("Database")]
public class HealthAndCanaryTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "Health")]
    public async Task Health_endpoint_reports_healthy_with_body_and_creates_the_canary_row()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var opsClient = factory.CreateOpsClient();

        using var response = await WaitForHealthyAsync(opsClient);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Be("Healthy");

        var canaryCount = await CountCanaryRowsAsync(fixture.DatabaseName);
        canaryCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Health")]
    public async Task Metrics_endpoint_reports_build_info_and_health_check_status()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var opsClient = factory.CreateOpsClient();

        using var readyResponse = await WaitForHealthyAsync(opsClient);
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var metricsBody = await WaitForMetricsContainingAsync(
            opsClient,
            "ledger_health_check_status{check=\"database\"",
            TimeSpan.FromSeconds(40));

        metricsBody.Should().Contain("ledger_build_info{");
        metricsBody.Should().Contain("version=");
        metricsBody.Should().Contain("commit=");
        metricsBody.Should().Contain("ledger_health_check_status{check=\"database\"");
        metricsBody.Should().Contain("ledger_health_check_status{check=\"data_protection_canary\"");
    }

    [Fact]
    [Trait("Category", "Health")]
    public async Task Api_port_does_not_serve_health_or_metrics()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var opsClient = factory.CreateOpsClient();
        using var apiClient = factory.CreateApiClient();

        using var readyResponse = await WaitForHealthyAsync(opsClient);
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var healthOnApi = await apiClient.GetAsync("/health", TestContext.Current.CancellationToken);
        using var metricsOnApi = await apiClient.GetAsync("/metrics", TestContext.Current.CancellationToken);

        healthOnApi.StatusCode.Should().NotBe(HttpStatusCode.OK);
        metricsOnApi.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "DataProtectionRestart")]
    public async Task Canary_survives_a_restart_with_a_different_content_root()
    {
        var contentRootA = Directory.CreateTempSubdirectory("ledger-content-root-a-").FullName;
        var contentRootB = Directory.CreateTempSubdirectory("ledger-content-root-b-").FullName;

        await using (var hostA = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"), contentRootA))
        {
            using var opsClientA = hostA.CreateOpsClient();
            using var responseA = await WaitForHealthyAsync(opsClientA);
            responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await using var hostB = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"), contentRootB);
        using var opsClientB = hostB.CreateOpsClient();
        using var responseB = await WaitForHealthyAsync(opsClientB);

        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "DataProtectionRestart")]
    public async Task Concurrent_first_starts_on_a_fresh_database_leave_exactly_one_canary_row()
    {
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);

        var connectionString = fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime");

        await using var hostA = new LedgerWebApplicationFactory(connectionString);
        await using var hostB = new LedgerWebApplicationFactory(connectionString);

        using var opsClientA = hostA.CreateOpsClient();
        using var opsClientB = hostB.CreateOpsClient();

        var responseTaskA = WaitForHealthyAsync(opsClientA);
        var responseTaskB = WaitForHealthyAsync(opsClientB);
        await Task.WhenAll(responseTaskA, responseTaskB);

        using var responseA = await responseTaskA;
        using var responseB = await responseTaskB;

        responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);

        var canaryCount = await CountCanaryRowsAsync(databaseName);
        canaryCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "DataProtectionRestart")]
    public async Task Losing_the_key_ring_reports_unhealthy_and_never_rewrites_the_canary()
    {
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);

        var connectionString = fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime");

        await using (var firstHost = new LedgerWebApplicationFactory(connectionString))
        {
            using var opsClient = firstHost.CreateOpsClient();
            using var response = await WaitForHealthyAsync(opsClient);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var (payloadBefore, hashBefore) = await ReadCanaryAsync(databaseName);

        await DeleteAllDataProtectionKeysAsync(databaseName);

        await using var secondHost = new LedgerWebApplicationFactory(connectionString);
        using var secondOpsClient = secondHost.CreateOpsClient();

        using var unhealthyResponse = await WaitForStatusAsync(secondOpsClient, HttpStatusCode.ServiceUnavailable);
        unhealthyResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var (payloadAfter, hashAfter) = await ReadCanaryAsync(databaseName);
        payloadAfter.Should().Be(payloadBefore);
        hashAfter.Should().Equal(hashBefore);
    }

    private async Task<int> CountCanaryRowsAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(databaseName, "ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM data_protection_canary";

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<(string ProtectedPayload, byte[] PlaintextSha256)> ReadCanaryAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(databaseName, "ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_payload, plaintext_sha256 FROM data_protection_canary WHERE id = 1";

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.GetString(0), (byte[])reader.GetValue(1));
    }

    private async Task DeleteAllDataProtectionKeysAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(databaseName, "ledger_migrator"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM data_protection_keys";
        await command.ExecuteNonQueryAsync();
    }

    private static Task<HttpResponseMessage> WaitForHealthyAsync(HttpClient client, TimeSpan? timeout = null) =>
        WaitForStatusAsync(client, HttpStatusCode.OK, timeout);

    private static async Task<string> WaitForMetricsContainingAsync(
        HttpClient client,
        string expectedSubstring,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(40));

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
                var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

                if (body.Contains(expectedSubstring, StringComparison.Ordinal))
                {
                    return body;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"/metrics never contained '{expectedSubstring}' within the timeout.");
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
}
