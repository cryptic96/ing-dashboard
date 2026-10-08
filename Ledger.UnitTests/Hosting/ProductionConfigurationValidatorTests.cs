using FluentAssertions;
using Ledger.Service.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ledger.UnitTests.Hosting;

/// <summary>Verifies the production configuration validator collects every problem into one exception naming only offending keys.</summary>
public class ProductionConfigurationValidatorTests : IDisposable
{
    private const string SentinelPassword = "sentinel-Tr0ub4dor-Value";
    private const string ValidConnectionString = "Host=/var/run/postgresql;Database=ledger;Username=ledger_runtime";
    private const string ValidApplicationId = "00000000-0000-0000-0000-000000000001";
    private const string DefaultKeyPath = "existing-key-file";
    private const string ValidKnownProxy = "192.0.2.10";
    private const string ValidRedirectUrl = "https://ledger-api.example.com/api/v1/bank/callback";
    private const string ValidApiUrl = "https://0.0.0.0:5080";
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
    [InlineData(null)]
    [InlineData("None")]
    public void ThrowIfInvalid_accepts_no_provider_without_any_bank_settings(string? provider)
    {
        var configuration = BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString, provider);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_accepts_enable_banking_with_a_complete_bank_configuration()
    {
        var configuration = CompleteEnableBankingConfiguration();

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("0000000000000000000000000000000z")]
    public void ThrowIfInvalid_names_only_the_application_id_key_when_it_is_missing_or_not_a_guid(string? applicationId)
    {
        var configuration = CompleteEnableBankingConfiguration(applicationId: applicationId);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("EnableBanking:ApplicationId");
        exception.Message.Should().NotContain("EnableBanking:PrivateKey");
        exception.Message.Should().NotContain("not-a-guid");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/nonexistent/enablebanking-key.pem")]
    public void ThrowIfInvalid_names_only_the_private_key_path_key_when_the_key_file_is_missing(string? keyPath)
    {
        var configuration = CompleteEnableBankingConfiguration(keyPath: keyPath);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("EnableBanking:PrivateKeyPath");
        exception.Message.Should().NotContain("EnableBanking:ApplicationId");
        exception.Message.Should().NotContain("EnableBanking:PrivateKeyPassword");
        exception.Message.Should().NotContain("nonexistent");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ThrowIfInvalid_names_only_the_private_key_password_key_when_it_is_empty(string? keyPassword)
    {
        var configuration = CompleteEnableBankingConfiguration(keyPassword: keyPassword);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("EnableBanking:PrivateKeyPassword");
        exception.Message.Should().NotContain("EnableBanking:ApplicationId");
        exception.Message.Should().NotContain("EnableBanking:PrivateKeyPath");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_never_shows_a_bank_setting_value()
    {
        var configuration = CompleteEnableBankingConfiguration(
            applicationId: "value-that-is-not-a-guid",
            keyPath: "/nonexistent/value-in-path.pem",
            keyPassword: " ");

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("EnableBanking:ApplicationId");
        exception.Message.Should().Contain("EnableBanking:PrivateKeyPath");
        exception.Message.Should().Contain("EnableBanking:PrivateKeyPassword");
        exception.Message.Should().NotContain("value-that-is-not-a-guid");
        exception.Message.Should().NotContain("value-in-path");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("traefik.example.com")]
    public void ThrowIfInvalid_names_only_the_known_proxies_key_when_enable_banking_has_no_parseable_proxy_address(string? knownProxy)
    {
        var configuration = CompleteEnableBankingConfiguration(knownProxy: knownProxy);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("ReverseProxy:KnownProxies");
        exception.Message.Should().NotContain("EnableBanking:");
        exception.Message.Should().NotContain("traefik.example.com");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_does_not_require_a_known_proxy_when_no_bank_provider_is_configured()
    {
        var configuration = BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString);

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

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_does_not_require_any_oauth_key_when_no_public_base_address_is_set()
    {
        var configuration = BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString);

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_accepts_a_valid_public_address_with_valid_sign_in_networks()
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?>());

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("traefik.example.com")]
    public void ThrowIfInvalid_requires_a_known_proxy_when_the_oauth_surface_is_on_without_a_bank_provider(string? knownProxy)
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = knownProxy });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("ReverseProxy:KnownProxies");
        exception.Message.Should().NotContain("OAuth:");
        exception.Message.Should().NotContain("traefik.example.com");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_names_the_known_proxies_key_once_when_the_bank_link_and_the_oauth_surface_both_need_it()
    {
        var configuration = BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            "EnableBanking",
            ValidRedirectUrl,
            applicationId: ValidApplicationId,
            keyPath: _existingCertificatePath,
            keyPassword: SentinelPassword,
            extra: new Dictionary<string, string?>
            {
                ["OAuth:PublicBaseUrl"] = "https://mcp.household.test",
                ["OAuth:SignInNetworks:0"] = "192.0.2.0/24"
            });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Split("ReverseProxy:KnownProxies").Length.Should().Be(2);
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("http://mcp.household.test")]
    [InlineData("https://mcp.household.test/path")]
    [InlineData("https://mcp.household.test/mcp")]
    [InlineData("https://mcp.household.test?x=1")]
    [InlineData("https://mcp.household.test#part")]
    [InlineData("https://mcp.household.test/")]
    [InlineData("https://mcp.example.com")]
    [InlineData("https://mcp.example.org")]
    [InlineData("https://example.net")]
    [InlineData("mcp.household.test")]
    public void ThrowIfInvalid_names_the_public_base_address_key_for_an_unsafe_address(string address)
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?> { ["OAuth:PublicBaseUrl"] = address });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("OAuth:PublicBaseUrl");
        exception.Message.Should().NotContain("OAuth:SignInNetworks");
        exception.Message.Should().NotContain("household.test");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("")]
    [InlineData("not-a-range")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("160.79.104.0/22")]
    [InlineData("160.79.104.0/21")]
    [InlineData("160.79.0.0/16")]
    [InlineData("160.79.111.255")]
    [InlineData("2607:6bc0::/32")]
    public void ThrowIfInvalid_names_the_sign_in_networks_key_for_an_empty_invalid_open_or_anthropic_overlapping_range(string range)
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?> { ["OAuth:SignInNetworks:0"] = range });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("OAuth:SignInNetworks");
        exception.Message.Should().NotContain("OAuth:PublicBaseUrl");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_names_the_sign_in_networks_key_when_no_range_is_configured()
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?>
        {
            ["OAuth:SignInNetworks:0"] = null,
            ["OAuth:SignInNetworks:1"] = null
        });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*OAuth:SignInNetworks*");
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_names_the_sign_in_networks_key_when_any_one_range_is_unsafe()
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?> { ["OAuth:SignInNetworks:1"] = "160.79.105.0/24" });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*OAuth:SignInNetworks*");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("OAuth:AccessTokenLifetime", "00:00:30")]
    [InlineData("OAuth:AccessTokenLifetime", "02:00:00")]
    [InlineData("OAuth:AccessTokenLifetime", "soon")]
    [InlineData("OAuth:RefreshTokenLifetime", "0.12:00:00")]
    [InlineData("OAuth:RefreshTokenLifetime", "365.00:00:00")]
    [InlineData("OAuth:RefreshTokenReuseLeeway", "00:05:00")]
    [InlineData("OAuth:RefreshTokenReuseLeeway", "-00:00:01")]
    public void ThrowIfInvalid_names_a_token_lifetime_key_that_is_outside_its_range(string key, string value)
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?> { [key] = value });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain(key);
        exception.Message.Should().NotContain(value);
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("OAuth:AccessTokenLifetime", "00:01:00")]
    [InlineData("OAuth:AccessTokenLifetime", "01:00:00")]
    [InlineData("OAuth:RefreshTokenLifetime", "1.00:00:00")]
    [InlineData("OAuth:RefreshTokenLifetime", "180.00:00:00")]
    [InlineData("OAuth:RefreshTokenReuseLeeway", "00:00:00")]
    [InlineData("OAuth:RefreshTokenReuseLeeway", "00:02:00")]
    public void ThrowIfInvalid_accepts_token_lifetimes_at_the_ends_of_their_ranges(string key, string value)
    {
        var configuration = OAuthConfiguration(new Dictionary<string, string?> { [key] = value });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_passes_for_an_https_endpoint_with_readable_certificate_and_key()
    {
        var configuration = ApiEndpointConfiguration(new Dictionary<string, string?>());

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        act.Should().NotThrow();
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("https://0.0.0.0:5080;http://0.0.0.0:5082")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData(null)]
    public void ThrowIfInvalid_names_the_api_url_key_when_the_proxy_facing_endpoint_is_not_https(string? url)
    {
        var configuration = ApiEndpointConfiguration(new Dictionary<string, string?> { ["Kestrel:Endpoints:Api:Url"] = url });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Kestrel:Endpoints:Api:Url");
        exception.Message.Should().NotContain("Certificate");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("Kestrel:Endpoints:Api:Certificate:Path")]
    [InlineData("Kestrel:Endpoints:Api:Certificate:KeyPath")]
    public void ThrowIfInvalid_names_the_certificate_key_when_its_file_is_not_configured(string key)
    {
        var configuration = ApiEndpointConfiguration(new Dictionary<string, string?> { [key] = null });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain(key);
        exception.Message.Should().NotContain("Kestrel:Endpoints:Api:Url");
    }

    [Theory]
    [Trait("Category", "Configuration")]
    [InlineData("Kestrel:Endpoints:Api:Certificate:Path")]
    [InlineData("Kestrel:Endpoints:Api:Certificate:KeyPath")]
    public void ThrowIfInvalid_names_the_certificate_key_when_its_file_is_missing_or_not_a_readable_file(string key)
    {
        foreach (var path in new[] { "/nonexistent/backend-tls.pem", Path.GetTempPath() })
        {
            var configuration = ApiEndpointConfiguration(new Dictionary<string, string?> { [key] = path });

            var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

            var exception = act.Should().Throw<InvalidOperationException>().Which;
            exception.Message.Should().Contain(key);
            exception.Message.Should().NotContain(path);
        }
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void ThrowIfInvalid_names_every_endpoint_key_at_once_and_no_values()
    {
        var configuration = ApiEndpointConfiguration(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Api:Url"] = "http://0.0.0.0:5080",
            ["Kestrel:Endpoints:Api:Certificate:Path"] = null,
            ["Kestrel:Endpoints:Api:Certificate:KeyPath"] = "/nonexistent/backend-tls.key"
        });

        var act = () => ProductionConfigurationValidator.ThrowIfInvalid(configuration);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("Kestrel:Endpoints:Api:Url");
        message.Should().Contain("Kestrel:Endpoints:Api:Certificate:Path");
        message.Should().Contain("Kestrel:Endpoints:Api:Certificate:KeyPath");
        message.Should().NotContain("0.0.0.0");
        message.Should().NotContain("/nonexistent");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        File.Delete(_existingCertificatePath);
    }

    private IConfiguration ApiEndpointConfiguration(IReadOnlyDictionary<string, string?> overrides)
    {
        return BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString, extra: overrides);
    }

    private IConfiguration OAuthConfiguration(IReadOnlyDictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["OAuth:PublicBaseUrl"] = "https://mcp.household.test",
            ["ReverseProxy:KnownProxies:0"] = ValidKnownProxy,
            ["OAuth:SignInNetworks:0"] = "192.0.2.0/24",
            ["OAuth:SignInNetworks:1"] = "198.51.100.0/24"
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return BuildConfiguration(_existingCertificatePath, SentinelPassword, ValidConnectionString, extra: values);
    }

    private IConfiguration CompleteEnableBankingConfiguration(
        string? applicationId = ValidApplicationId,
        string? keyPath = DefaultKeyPath,
        string? keyPassword = SentinelPassword,
        string? knownProxy = ValidKnownProxy)
    {
        return BuildConfiguration(
            _existingCertificatePath,
            SentinelPassword,
            ValidConnectionString,
            "EnableBanking",
            ValidRedirectUrl,
            applicationId: applicationId,
            keyPath: keyPath == DefaultKeyPath ? _existingCertificatePath : keyPath,
            keyPassword: keyPassword,
            knownProxy: knownProxy);
    }

    private IConfiguration BuildConfiguration(
        string? certificatePath,
        string? certificatePassword,
        string connectionString,
        string? provider = null,
        string? redirectUrl = null,
        string? timeZone = null,
        string? scheduleTime = null,
        string? applicationId = null,
        string? keyPath = null,
        string? keyPassword = null,
        string? knownProxy = null,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Api:Url"] = ValidApiUrl,
            ["Kestrel:Endpoints:Api:Certificate:Path"] = _existingCertificatePath,
            ["Kestrel:Endpoints:Api:Certificate:KeyPath"] = _existingCertificatePath,
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = certificatePassword,
            ["ConnectionStrings:Ledger"] = connectionString,
            ["Ingestion:Provider"] = provider,
            ["BankLink:RedirectUrl"] = redirectUrl,
            ["Ingestion:TimeZone"] = timeZone,
            ["Ingestion:ScheduleLocalTime"] = scheduleTime,
            ["EnableBanking:ApplicationId"] = applicationId,
            ["EnableBanking:PrivateKeyPath"] = keyPath,
            ["EnableBanking:PrivateKeyPassword"] = keyPassword,
            ["ReverseProxy:KnownProxies:0"] = knownProxy
        };

        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
