using FluentAssertions;
using Ledger.Service.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ledger.UnitTests.Hosting;

/// <summary>Verifies the production configuration validator collects every problem into one exception naming only offending keys.</summary>
public class ProductionConfigurationValidatorTests : IDisposable
{
    private const string SentinelPassword = "sentinel-Tr0ub4dor-Value";
    private const string ValidConnectionString = "Host=/var/run/postgresql;Database=ledger;Username=ledger_runtime";
    private readonly string _existingCertificatePath = Path.GetTempFileName();

    [Fact]
    public void ThrowIfInvalid_passes_for_a_complete_valid_configuration()
    {
        var configuration = BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfInvalid_fails_when_certificate_path_is_missing()
    {
        var configuration = BuildConfiguration(null, SentinelPassword, ValidConnectionString);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DataProtection:CertificatePath*");
    }

    [Fact]
    public void ThrowIfInvalid_fails_when_certificate_path_does_not_exist()
    {
        var configuration = BuildConfiguration("/nonexistent/ledger-cert.pfx", SentinelPassword, ValidConnectionString);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DataProtection:CertificatePath*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ThrowIfInvalid_fails_when_certificate_password_is_empty_or_whitespace(string certificatePassword)
    {
        var configuration = BuildConfiguration(_existingCertificatePath, certificatePassword, ValidConnectionString);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DataProtection:CertificatePassword*");
    }

    [Theory]
    [InlineData("Host=localhost;Database=ledger;Username=ledger_runtime")]
    [InlineData("Host=/var/run/postgresql;Database=ledger;Username=ledger_runtime;Password=" + SentinelPassword)]
    [InlineData("Host=/var/run/postgresql;Database=ledger;Username=postgres")]
    [InlineData("Host=/var/run/postgresql;Database=ledger;Username=ledger_runtime;Include Error Detail=true")]
    public void ThrowIfInvalid_fails_for_each_unsafe_connection_string(string connectionString)
    {
        var configuration = BuildConfiguration(_existingCertificatePath, SentinelPassword, connectionString);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("ConnectionStrings:Ledger");
        exception.Message.Should().NotContain(SentinelPassword);
    }

    [Fact]
    public void ThrowIfInvalid_names_exactly_three_offending_keys_and_no_values()
    {
        var configuration = BuildConfiguration(null, "", "Host=localhost;Database=ledger;Username=ledger_runtime");

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("DataProtection:CertificatePath");
        exception.Message.Should().Contain("DataProtection:CertificatePassword");
        exception.Message.Should().Contain("ConnectionStrings:Ledger");
        exception.Message.Should().NotContain("localhost");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        File.Delete(_existingCertificatePath);
    }

    private static IConfiguration BuildConfiguration(
        string? certificatePath,
        string? certificatePassword,
        string connectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = certificatePassword,
            ["ConnectionStrings:Ledger"] = connectionString
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
