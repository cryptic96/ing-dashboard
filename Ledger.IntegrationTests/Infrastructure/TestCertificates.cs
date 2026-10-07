using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Creates disposable self-signed test certificates for Data Protection key-ring tests.</summary>
public static class TestCertificates
{
    private const string SharedPassword = "ledger-test-shared-key-ring-password";

    private static readonly Lazy<string> SharedPath = new(() => CreateSelfSignedPfx(SharedPassword));

    /// <summary>
    /// The configuration that points a host or an operator command at one certificate shared by the whole test run, so every
    /// host on one database opens the same key ring.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> SharedKeyRingSettings => new Dictionary<string, string?>
    {
        ["DataProtection:CertificatePath"] = SharedPath.Value,
        ["DataProtection:CertificatePassword"] = SharedPassword
    };

    /// <summary>Creates a self-signed RSA certificate and writes a password-protected PFX to a new temp file, returning its path.</summary>
    public static string CreateSelfSignedPfx(string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ledger-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        var pfxBytes = certificate.Export(X509ContentType.Pkcs12, password);

        var path = Path.Combine(Path.GetTempPath(), $"ledger-test-cert-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfxBytes);

        return path;
    }
}
