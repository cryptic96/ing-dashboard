using Ledger.Repository;
using Microsoft.Extensions.Configuration;

namespace Ledger.Service.Hosting;

/// <summary>Fails startup with one exception naming every offending configuration key, never a value, before the host is built.</summary>
public static class ProductionConfigurationValidator
{
    private const string CertificatePathKey = "DataProtection:CertificatePath";
    private const string CertificatePasswordKey = "DataProtection:CertificatePassword";
    private const string ConnectionStringKey = "ConnectionStrings:Ledger";

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

        if (offendingKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unsafe or missing required configuration key(s): {string.Join(", ", offendingKeys)}.");
        }
    }
}
