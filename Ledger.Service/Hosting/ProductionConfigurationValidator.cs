using System.Globalization;
using Ledger.Domain.Ingestion;
using Ledger.Repository;
using Ledger.Service.Ingestion;
using Microsoft.Extensions.Configuration;

namespace Ledger.Service.Hosting;

/// <summary>Fails startup with one exception naming every offending configuration key, never a value, before the host is built.</summary>
public static class ProductionConfigurationValidator
{
    private const string CertificatePathKey = "DataProtection:CertificatePath";
    private const string CertificatePasswordKey = "DataProtection:CertificatePassword";
    private const string ConnectionStringKey = "ConnectionStrings:Ledger";
    private const string ProviderKey = "Ingestion:Provider";
    private const string RedirectUrlKey = "BankLink:RedirectUrl";
    private const string ApplicationIdKey = "EnableBanking:ApplicationId";
    private const string PrivateKeyPathKey = "EnableBanking:PrivateKeyPath";
    private const string PrivateKeyPasswordKey = "EnableBanking:PrivateKeyPassword";
    private const string TimeZoneKey = "Ingestion:TimeZone";
    private const string ScheduleLocalTimeKey = "Ingestion:ScheduleLocalTime";

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

        AddBankLinkProblems(configuration, offendingKeys);
        AddScheduleProblems(configuration, offendingKeys);

        if (offendingKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unsafe or missing required configuration key(s): {string.Join(", ", offendingKeys)}.");
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
