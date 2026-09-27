using System.Security.Cryptography.X509Certificates;
using Ledger.Repository;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledger.Service.Security;

/// <summary>Configures the Data Protection key ring, encrypting it at rest with a certificate whenever one is configured.</summary>
public static class DataProtectionSetup
{
    private const string CertificatePathKey = "DataProtection:CertificatePath";
    private const string CertificatePasswordKey = "DataProtection:CertificatePassword";

    /// <summary>Persists keys to the ledger database and, when a certificate is configured, protects and unprotects them with it. A certificate is required in Production.</summary>
    public static IServiceCollection AddLedgerDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var certificatePath = configuration[CertificatePathKey];

        if (environment.IsProduction() && string.IsNullOrWhiteSpace(certificatePath))
        {
            throw new InvalidOperationException($"{CertificatePathKey} is required in Production.");
        }

        var dataProtectionBuilder = services
            .AddDataProtection()
            .SetApplicationName("HouseholdLedger")
            .PersistKeysToLedgerDatabase();

        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            return services;
        }

        var certificatePassword = configuration[CertificatePasswordKey];
        var certificate = LoadCertificate(certificatePath, certificatePassword);

        dataProtectionBuilder
            .ProtectKeysWithCertificate(certificate)
            .UnprotectKeysWithAnyCertificate(certificate);

        return services;
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not load the certificate configured at {CertificatePathKey} with {CertificatePasswordKey}.");
        }
    }
}
