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

/// <summary>
/// A throwaway certificate and private key written as the PEM pair the proxy-facing endpoint is configured with: an EC P-256
/// certificate for the internal backend name, as the host generates it. The files are deleted on disposal.
/// </summary>
public sealed class TestBackendCertificate : IDisposable
{
    private readonly string _directory;

    /// <summary>Creates a new certificate and key pair in a new temp directory.</summary>
    public TestBackendCertificate()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"ledger-test-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ledger-backend", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("ledger-backend");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        CertificatePath = Path.Combine(_directory, "backend-tls.crt");
        KeyPath = Path.Combine(_directory, "backend-tls.key");
        File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(KeyPath, key.ExportPkcs8PrivateKeyPem());
        Certificate = X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
    }

    /// <summary>The path of the public certificate in PEM form.</summary>
    public string CertificatePath { get; }

    /// <summary>The path of the private key in PEM form.</summary>
    public string KeyPath { get; }

    /// <summary>The public certificate a client pins.</summary>
    public X509Certificate2 Certificate { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Certificate.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
