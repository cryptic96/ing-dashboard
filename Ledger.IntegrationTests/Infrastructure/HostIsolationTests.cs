using FluentAssertions;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Proves the test host factory leaves the process the way it found it.</summary>
[Collection("Database")]
public class HostIsolationTests(DatabaseFixture fixture)
{
    private const string ProviderVariable = "Ingestion__Provider";
    private const string CertificatePathVariable = "DataProtection__CertificatePath";
    private const string CertificatePasswordVariable = "DataProtection__CertificatePassword";

    [Fact]
    public void An_override_puts_back_the_previous_value_and_the_previous_absence()
    {
        const string present = "LEDGER_TEST_OVERRIDE_PRESENT";
        const string absent = "LEDGER_TEST_OVERRIDE_ABSENT";

        using (new EnvironmentOverride(present, "before"))
        {
            using (new EnvironmentOverride(
            [
                new KeyValuePair<string, string?>(present, "during"),
                new KeyValuePair<string, string?>(absent, "during")
            ]))
            {
                Environment.GetEnvironmentVariable(present).Should().Be("during");
                Environment.GetEnvironmentVariable(absent).Should().Be("during");
            }

            Environment.GetEnvironmentVariable(present).Should().Be("before");
            Environment.GetEnvironmentVariable(absent).Should().BeNull();
        }

        Environment.GetEnvironmentVariable(present).Should().BeNull();
    }

    [Fact]
    public void A_started_host_ignores_ambient_values_during_startup_and_restores_them_afterwards()
    {
        using var ambient = new EnvironmentOverride(
        [
            new KeyValuePair<string, string?>(ProviderVariable, "EnableBanking"),
            new KeyValuePair<string, string?>(CertificatePathVariable, "/ambient/value.pfx"),
            new KeyValuePair<string, string?>(CertificatePasswordVariable, "ambient-value")
        ]);

        using var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            startupEnvironment: new Dictionary<string, string?> { [ProviderVariable] = "None" });

        Environment.GetEnvironmentVariable(ProviderVariable).Should().Be("EnableBanking");
        Environment.GetEnvironmentVariable(CertificatePathVariable).Should().Be("/ambient/value.pfx");
        Environment.GetEnvironmentVariable(CertificatePasswordVariable).Should().Be("ambient-value");
    }

    [Fact]
    public void A_host_that_fails_to_start_still_restores_the_environment()
    {
        using var ambient = new EnvironmentOverride(CertificatePasswordVariable, "ambient-value");

        var act = () => new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            certificatePath: "/nonexistent/isolation-test.pfx",
            certificatePassword: "startup-value");

        act.Should().Throw<Exception>();
        Environment.GetEnvironmentVariable(CertificatePasswordVariable).Should().Be("ambient-value");
        Environment.GetEnvironmentVariable(CertificatePathVariable).Should().BeNull();
    }
}
