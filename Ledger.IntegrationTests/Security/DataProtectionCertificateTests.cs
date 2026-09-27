using System.Net;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;

namespace Ledger.IntegrationTests.Security;

/// <summary>Proves the certificate-protected key ring survives restart and redeploy, and that a wrong certificate is reported, never healed.</summary>
[Collection("Database")]
public class DataProtectionCertificateTests(DatabaseFixture fixture)
{
    private const string CertificatePassword = "ledger-test-certificate-password";

    [Fact]
    [Trait("Category", "DataProtection")]
    public async Task Key_rows_are_encrypted_with_the_certificate_and_never_store_a_plaintext_master_key()
    {
        var certificatePath = TestCertificates.CreateSelfSignedPfx(CertificatePassword);
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);

        await using (var host = new LedgerWebApplicationFactory(
            fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime"),
            certificatePath: certificatePath,
            certificatePassword: CertificatePassword))
        {
            using var opsClient = host.CreateOpsClient();
            using var response = await WaitForStatusAsync(opsClient, HttpStatusCode.OK);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var xmlRows = await ReadKeyXmlAsync(databaseName);

        xmlRows.Should().NotBeEmpty();
        xmlRows.Should().OnlyContain(xml => xml.Contains("encryptedSecret"));
        xmlRows.Should().OnlyContain(xml => !xml.Contains("<value>"));
    }

    [Fact]
    [Trait("Category", "DataProtectionRestart")]
    public async Task Restart_with_a_different_content_root_decrypts_with_the_same_certificate()
    {
        var certificatePath = TestCertificates.CreateSelfSignedPfx(CertificatePassword);
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);
        var connectionString = fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime");

        var contentRootA = Directory.CreateTempSubdirectory("ledger-dp-cert-a-").FullName;
        var contentRootB = Directory.CreateTempSubdirectory("ledger-dp-cert-b-").FullName;

        await using (var hostA = new LedgerWebApplicationFactory(
            connectionString, contentRootA, certificatePath, CertificatePassword))
        {
            using var opsClientA = hostA.CreateOpsClient();
            using var responseA = await WaitForStatusAsync(opsClientA, HttpStatusCode.OK);
            responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await using var hostB = new LedgerWebApplicationFactory(
            connectionString, contentRootB, certificatePath, CertificatePassword);
        using var opsClientB = hostB.CreateOpsClient();
        using var responseB = await WaitForStatusAsync(opsClientB, HttpStatusCode.OK);

        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "DataProtection")]
    public async Task Wrong_certificate_reports_unhealthy_and_leaves_the_canary_row_unchanged()
    {
        var correctCertificatePath = TestCertificates.CreateSelfSignedPfx(CertificatePassword);
        var wrongCertificatePath = TestCertificates.CreateSelfSignedPfx(CertificatePassword);
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);
        var connectionString = fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime");

        await using (var hostA = new LedgerWebApplicationFactory(
            connectionString, certificatePath: correctCertificatePath, certificatePassword: CertificatePassword))
        {
            using var opsClientA = hostA.CreateOpsClient();
            using var responseA = await WaitForStatusAsync(opsClientA, HttpStatusCode.OK);
            responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var (payloadBefore, hashBefore) = await ReadCanaryAsync(databaseName);

        await using var hostC = new LedgerWebApplicationFactory(
            connectionString, certificatePath: wrongCertificatePath, certificatePassword: CertificatePassword);
        using var opsClientC = hostC.CreateOpsClient();
        using var responseC = await WaitForStatusAsync(opsClientC, HttpStatusCode.ServiceUnavailable);

        responseC.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var (payloadAfter, hashAfter) = await ReadCanaryAsync(databaseName);
        payloadAfter.Should().Be(payloadBefore);
        hashAfter.Should().Equal(hashBefore);
    }

    [Fact]
    [Trait("Category", "DataProtection")]
    public void Unreadable_certificate_path_fails_startup_naming_only_the_path_key()
    {
        var act = () => new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            certificatePath: "/nonexistent/ledger-test-cert.pfx",
            certificatePassword: CertificatePassword);

        var exception = act.Should().Throw<Exception>().Which;
        var messages = AllMessages(exception).ToList();

        messages.Should().Contain(message => message.Contains("DataProtection:CertificatePath"));
        messages.Should().NotContain(message => message.Contains(CertificatePassword));
    }

    [Fact]
    [Trait("Category", "DataProtection")]
    public void Wrong_certificate_password_fails_startup_naming_only_the_password_key()
    {
        var certificatePath = TestCertificates.CreateSelfSignedPfx(CertificatePassword);

        var act = () => new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            certificatePath: certificatePath,
            certificatePassword: "definitely-the-wrong-password");

        var exception = act.Should().Throw<Exception>().Which;
        var messages = AllMessages(exception).ToList();

        messages.Should().Contain(message => message.Contains("DataProtection:CertificatePassword"));
        messages.Should().NotContain(message => message.Contains(CertificatePassword));
        messages.Should().NotContain(message => message.Contains("definitely-the-wrong-password"));
    }

    private static IEnumerable<string> AllMessages(Exception? exception)
    {
        while (exception is not null)
        {
            yield return exception.Message;
            exception = exception.InnerException;
        }
    }

    private async Task<List<string>> ReadKeyXmlAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(databaseName, "ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT xml FROM data_protection_keys";

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();

        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return rows;
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
