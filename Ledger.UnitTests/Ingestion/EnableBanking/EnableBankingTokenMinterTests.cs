using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.EnableBanking;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Ledger.UnitTests.Ingestion.EnableBanking;

/// <summary>Verifies the client tokens that authorise requests to the aggregator.</summary>
[Trait("Category", "EnableBanking")]
public sealed class EnableBankingTokenMinterTests : IDisposable
{
    private const string ApplicationId = "00000000-0000-0000-0000-000000000001";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ledger-minter-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    public EnableBankingTokenMinterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Token_header_and_claims_match_what_the_aggregator_expects_and_the_signature_verifies()
    {
        var minter = CreateMinter(password: null);

        var parts = minter.Create().Split('.');

        parts.Should().HaveCount(3);
        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        header.GetProperty("alg").GetString().Should().Be("RS256");
        header.GetProperty("typ").GetString().Should().Be("JWT");
        header.GetProperty("kid").GetString().Should().Be(ApplicationId);

        var payload = JsonDocument.Parse(Decode(parts[1])).RootElement;
        payload.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("iss", "aud", "iat", "exp");
        payload.GetProperty("iss").GetString().Should().Be("enablebanking.com");
        payload.GetProperty("aud").GetString().Should().Be("api.enablebanking.com");
        payload.GetProperty("exp").GetInt64().Should().Be(payload.GetProperty("iat").GetInt64() + 1800);
        payload.GetProperty("iat").GetInt64().Should().Be(_time.GetUtcNow().ToUnixTimeSeconds());

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = Convert.FromBase64String(PadBase64Url(parts[2]));
        _rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Fact]
    public void An_encrypted_key_is_read_with_its_password()
    {
        var minter = CreateMinter(password: "synthetic-test-password");

        minter.Create().Split('.').Should().HaveCount(3);
    }

    [Fact]
    public void A_second_token_within_its_lifetime_is_the_same_and_a_new_one_follows_before_expiry()
    {
        var minter = CreateMinter(password: null);

        var first = minter.Create();
        _time.Advance(TimeSpan.FromMinutes(20));
        minter.Create().Should().Be(first);

        _time.Advance(TimeSpan.FromMinutes(6));
        minter.Create().Should().NotBe(first);
    }

    [Fact]
    public void A_wrong_password_or_missing_file_names_only_the_configuration_key()
    {
        var wrongPassword = CreateMinter(password: "synthetic-test-password", passwordToUse: "another-synthetic-password");
        var missingFile = CreateMinter(password: null, keyPathOverride: Path.Combine(_directory, "absent.pem"));

        foreach (var minter in new[] { wrongPassword, missingFile })
        {
            var thrown = minter.Invoking(candidate => candidate.Create()).Should().Throw<BankProviderException>().Which;
            thrown.Kind.Should().Be(ProviderErrorKind.ProviderAuth);
            thrown.Message.Should().Contain("EnableBanking:PrivateKeyPath")
                .And.NotContain("another-synthetic-password")
                .And.NotContain(_directory);
        }
    }

    [Fact]
    public void A_missing_application_id_names_only_its_configuration_key()
    {
        var minter = CreateMinter(password: null, applicationId: null);

        var thrown = minter.Invoking(candidate => candidate.Create()).Should().Throw<BankProviderException>().Which;

        thrown.Message.Should().Contain("EnableBanking:ApplicationId");
    }

    private EnableBankingTokenMinter CreateMinter(
        string? password,
        string? passwordToUse = null,
        string? keyPathOverride = null,
        string? applicationId = ApplicationId)
    {
        var path = Path.Combine(_directory, "key-" + Guid.NewGuid().ToString("N") + ".pem");
        var pem = password is null ? _rsa.ExportPkcs8PrivateKeyPem() : _rsa.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        File.WriteAllText(path, pem);

        var options = new EnableBankingOptions
        {
            ApplicationId = applicationId,
            PrivateKeyPath = keyPathOverride ?? path,
            PrivateKeyPassword = passwordToUse ?? password
        };

        return new EnableBankingTokenMinter(Options.Create(options), _time);
    }

    private static string Decode(string base64Url)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64Url(base64Url)));
    }

    private static string PadBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
    }
}
