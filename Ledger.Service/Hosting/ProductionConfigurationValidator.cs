using System.Globalization;
using System.Net;
using Ledger.Domain.Ingestion;
using Ledger.Repository;
using Ledger.Service.Ingestion;
using Ledger.Service.OAuth;
using Microsoft.Extensions.Configuration;

namespace Ledger.Service.Hosting;

/// <summary>Fails startup with one exception naming every offending configuration key, never a value, before the host is built.</summary>
public static class ProductionConfigurationValidator
{
    private const string CertificatePathKey = "DataProtection:CertificatePath";
    private const string CertificatePasswordKey = "DataProtection:CertificatePassword";
    private const string ConnectionStringKey = "ConnectionStrings:Ledger";
    private const string ApiUrlKey = "Kestrel:Endpoints:Api:Url";
    private const string ApiCertificatePathKey = "Kestrel:Endpoints:Api:Certificate:Path";
    private const string ApiCertificateKeyPathKey = "Kestrel:Endpoints:Api:Certificate:KeyPath";
    private const string ProviderKey = "Ingestion:Provider";
    private const string RedirectUrlKey = "BankLink:RedirectUrl";
    private const string ApplicationIdKey = "EnableBanking:ApplicationId";
    private const string PrivateKeyPathKey = "EnableBanking:PrivateKeyPath";
    private const string PrivateKeyPasswordKey = "EnableBanking:PrivateKeyPassword";
    private const string TimeZoneKey = "Ingestion:TimeZone";
    private const string ScheduleLocalTimeKey = "Ingestion:ScheduleLocalTime";
    private const string KnownProxiesKey = "ReverseProxy:KnownProxies";
    private const string PublicBaseUrlKey = "OAuth:PublicBaseUrl";
    private const string SignInNetworksKey = "OAuth:SignInNetworks";
    private const string AccessTokenLifetimeKey = "OAuth:AccessTokenLifetime";
    private const string RefreshTokenLifetimeKey = "OAuth:RefreshTokenLifetime";
    private const string RefreshTokenReuseLeewayKey = "OAuth:RefreshTokenReuseLeeway";

    private static readonly string[] ExampleDomains = ["example.com", "example.org", "example.net"];

    /// <summary>Throws one InvalidOperationException listing every offending configuration key when the Production configuration is unsafe.</summary>
    public static void ThrowIfInvalid(IConfiguration configuration)
    {
        var offendingKeys = new List<string>();

        var certificatePath = configuration[CertificatePathKey];
        if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
        {
            offendingKeys.Add(CertificatePathKey);
        }

        var certificatePassword = configuration[CertificatePasswordKey];
        if (string.IsNullOrWhiteSpace(certificatePassword))
        {
            offendingKeys.Add(CertificatePasswordKey);
        }

        var connectionString = configuration.GetConnectionString("Ledger");
        if (LedgerConnectionStringRules.Problems(connectionString).Count > 0)
        {
            offendingKeys.Add(ConnectionStringKey);
        }

        AddApiEndpointProblems(configuration, offendingKeys);
        AddBankLinkProblems(configuration, offendingKeys);
        AddScheduleProblems(configuration, offendingKeys);
        AddOAuthProblems(configuration, offendingKeys);

        if (offendingKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unsafe or missing required configuration key(s): {string.Join(", ", offendingKeys)}.");
        }
    }

    /// <summary>
    /// The endpoint the reverse proxy connects to carries passwords, one-time codes, tokens, session cookies and every answer the
    /// ledger gives, so in Production it must be https, with the certificate and its private key both configured as readable
    /// files. A plain-http endpoint, or one whose certificate cannot be read, stops the host before it serves anything.
    /// </summary>
    private static void AddApiEndpointProblems(IConfiguration configuration, List<string> offendingKeys)
    {
        if (!IsHttpsOnly(configuration[ApiUrlKey]))
        {
            offendingKeys.Add(ApiUrlKey);
        }

        if (!IsReadableFile(configuration[ApiCertificatePathKey]))
        {
            offendingKeys.Add(ApiCertificatePathKey);
        }

        if (!IsReadableFile(configuration[ApiCertificateKeyPathKey]))
        {
            offendingKeys.Add(ApiCertificateKeyPathKey);
        }
    }

    private static bool IsHttpsOnly(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return false;
        }

        var addresses = urls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return addresses.Length > 0 && addresses.All(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReadableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The sign-in, OAuth and MCP surface is switched on by the public base address, so its other keys are judged only when that
    /// address is present. The address must be a real https origin, the reverse proxy must be a trusted address, the sign-in networks must be a deliberate allow-list that
    /// never covers Anthropic's connectors, and the token lifetimes must stay in a sane range.
    /// </summary>
    private static void AddOAuthProblems(IConfiguration configuration, List<string> offendingKeys)
    {
        var publicBaseUrl = configuration[PublicBaseUrlKey];

        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return;
        }

        if (!IsValidPublicBaseUrl(publicBaseUrl))
        {
            offendingKeys.Add(PublicBaseUrlKey);
        }

        if (!HasValidSignInNetworks(configuration))
        {
            offendingKeys.Add(SignInNetworksKey);
        }

        AddReverseProxyProblems(configuration, offendingKeys);
        AddLifetimeProblem(configuration, AccessTokenLifetimeKey, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60), offendingKeys);
        AddLifetimeProblem(configuration, RefreshTokenLifetimeKey, TimeSpan.FromDays(1), TimeSpan.FromDays(180), offendingKeys);
        AddLifetimeProblem(configuration, RefreshTokenReuseLeewayKey, TimeSpan.Zero, TimeSpan.FromSeconds(120), offendingKeys);
    }

    private static bool IsValidPublicBaseUrl(string publicBaseUrl)
    {
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || publicBaseUrl.EndsWith('/')
            || publicBaseUrl.Contains('?', StringComparison.Ordinal)
            || publicBaseUrl.Contains('#', StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/")
        {
            return false;
        }

        return !ExampleDomains.Any(domain =>
            uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// At least one range is required, every range must parse, none may be a /0 that admits the whole internet, and none may
    /// overlap the ranges Anthropic's connectors call from (160.79.104.0/21 for IPv4 and its IPv6 counterpart), because those must
    /// never reach the sign-in pages.
    /// </summary>
    private static bool HasValidSignInNetworks(IConfiguration configuration)
    {
        var ranges = configuration.GetSection(SignInNetworksKey).Get<string[]>() ?? [];

        if (ranges.Length == 0)
        {
            return false;
        }

        var anthropic = new[] { SignInNetworks.AnthropicIpv4Range, SignInNetworks.AnthropicIpv6Range }
            .Select(range => SignInNetworks.TryParse(range, out var network) ? network : throw new InvalidOperationException("The Anthropic range constant is invalid."))
            .ToList();

        foreach (var range in ranges)
        {
            if (!SignInNetworks.TryParse(range, out var network)
                || network.PrefixLength == 0
                || anthropic.Any(blocked => SignInNetworks.Overlaps(network, blocked)))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddLifetimeProblem(
        IConfiguration configuration,
        string key,
        TimeSpan minimum,
        TimeSpan maximum,
        List<string> offendingKeys)
    {
        var text = configuration[key];

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
        {
            offendingKeys.Add(key);
        }
    }

    private static void AddScheduleProblems(IConfiguration configuration, List<string> offendingKeys)
    {
        var defaults = new IngestionOptions();
        var zoneId = configuration[TimeZoneKey] ?? defaults.TimeZone;
        var timeText = configuration[ScheduleLocalTimeKey] ?? defaults.ScheduleLocalTime;

        TimeZoneInfo? zone = null;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            offendingKeys.Add(TimeZoneKey);
        }

        if (!TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            offendingKeys.Add(ScheduleLocalTimeKey);
            return;
        }

        if (zone is not null && SkippedOnAnyDayOfTheNextYear(time, zone))
        {
            offendingKeys.Add(ScheduleLocalTimeKey);
        }
    }

    private static bool SkippedOnAnyDayOfTheNextYear(TimeOnly time, TimeZoneInfo zone)
    {
        var first = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var offset = 0; offset <= 400; offset++)
        {
            if (SyncSchedule.IsNonexistentLocalTime(first.AddDays(offset), time, zone))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddBankLinkProblems(IConfiguration configuration, List<string> offendingKeys)
    {
        var provider = configuration[ProviderKey];

        if (string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, IngestionOptions.Providers.None, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(provider, IngestionOptions.Providers.EnableBanking, StringComparison.OrdinalIgnoreCase))
        {
            offendingKeys.Add(ProviderKey);
            return;
        }

        var redirectUrl = configuration[RedirectUrlKey];
        var isHttps = Uri.TryCreate(redirectUrl, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps;

        if (!isHttps)
        {
            offendingKeys.Add(RedirectUrlKey);
        }

        AddAggregatorCredentialProblems(configuration, offendingKeys);
        AddReverseProxyProblems(configuration, offendingKeys);
    }

    /// <summary>
    /// The address a request comes from is only the real one when the reverse proxy's own address is trusted to forward it. The
    /// bank is told the operator's address, and the sign-in network check and the per-address rate limits judge the caller's, so
    /// both the bank link and the OAuth surface need it. At least one parseable proxy address is therefore required, and an entry
    /// that cannot be parsed is rejected rather than silently ignored. The key is named once however many features need it.
    /// </summary>
    private static void AddReverseProxyProblems(IConfiguration configuration, List<string> offendingKeys)
    {
        if (offendingKeys.Contains(KnownProxiesKey))
        {
            return;
        }

        var proxies = configuration.GetSection(KnownProxiesKey).Get<string[]>() ?? [];

        if (proxies.Length == 0 || proxies.Any(proxy => !IPAddress.TryParse(proxy, out _)))
        {
            offendingKeys.Add(KnownProxiesKey);
        }
    }

    private static void AddAggregatorCredentialProblems(IConfiguration configuration, List<string> offendingKeys)
    {
        if (!Guid.TryParse(configuration[ApplicationIdKey], out _))
        {
            offendingKeys.Add(ApplicationIdKey);
        }

        var keyPath = configuration[PrivateKeyPathKey];
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            offendingKeys.Add(PrivateKeyPathKey);
        }

        if (string.IsNullOrWhiteSpace(configuration[PrivateKeyPasswordKey]))
        {
            offendingKeys.Add(PrivateKeyPasswordKey);
        }
    }
}
