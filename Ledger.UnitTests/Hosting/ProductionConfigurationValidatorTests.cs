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

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("Synthetic")]
    [InlineData("synthetic")]
    [InlineData("SomethingElseEntirely")]
    public void ThrowIfInvalid_names_the_provider_key_for_synthetic_or_unknown_providers(string provider)
    {
        var configuration = BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString, provider);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Ingestion:Provider");
        exception.Message.Should().NotContain(provider);
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://ledger-api.example.com/api/v1/bank/callback")]
    [InlineData("/api/v1/bank/callback")]
    public void ThrowIfInvalid_names_the_redirect_key_when_enable_banking_has_no_https_redirect(string? redirectUrl)
    {
        var configuration = BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            "EnableBanking",
            redirectUrl);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("BankLink:RedirectUrl");
        exception.Message.Should().NotContain("Ingestion:Provider");
        exception.Message.Should().NotContain("example.com");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null, null)]
    [InlineData("None", null)]
    [InlineData("EnableBanking", "https://ledger-api.example.com/api/v1/bank/callback")]
    public void ThrowIfInvalid_accepts_no_provider_and_enable_banking_with_an_https_redirect(string? provider, string? redirectUrl)
    {
        var configuration = BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            provider,
            redirectUrl);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("Nowhere/Atlantis")]
    [InlineData("   ")]
    public void ThrowIfInvalid_names_the_time_zone_key_when_the_zone_cannot_be_resolved(string timeZone)
    {
        var configuration = BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            timeZone: timeZone);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Ingestion:TimeZone");
        exception.Message.Should().NotContain("Atlantis");
        exception.Message.Should().NotContain("Ingestion:ScheduleLocalTime");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("6:30 am")]
    [InlineData("25:00")]
    [InlineData("")]
    [InlineData("0630")]
    [InlineData("02:30")]
    public void ThrowIfInvalid_names_the_schedule_time_key_when_the_time_is_unparseable_or_skipped_by_a_clock_change(string scheduleTime)
    {
        var configuration = BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            scheduleTime: scheduleTime);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Ingestion:ScheduleLocalTime");
        exception.Message.Should().NotContain("Ingestion:TimeZone");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_accepts_a_custom_zone_and_a_time_that_always_exists()
    {
        var configuration = BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            timeZone: "Europe/London",
            scheduleTime: "07:15");

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        File.Delete(_existingCertificatePath);
    }

    private static IConfiguration BuildConfiguration(
        string? certificatePath,
        string? certificatePassword,
        string connectionString,
        string? provider = null,
        string? redirectUrl = null,
        string? timeZone = null,
        string? scheduleTime = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = certificatePassword,
            ["ConnectionStrings:Ledger"] = connectionString,
            ["Ingestion:Provider"] = provider,
            ["BankLink:RedirectUrl"] = redirectUrl,
            ["Ingestion:TimeZone"] = timeZone,
            ["Ingestion:ScheduleLocalTime"] = scheduleTime
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
