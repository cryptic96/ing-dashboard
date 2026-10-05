using System.Security.Cryptography;
using Ledger.Domain.Banking;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>
/// Mints the short-lived RS256 client tokens that authorise requests to the aggregator, signed with the application's private
/// key. The key is read on first use and never leaves this class; failures name only the configuration key involved.
/// </summary>
public sealed class EnableBankingTokenMinter(IOptions<EnableBankingOptions> options, TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private RSA? _rsa;
    private RsaSecurityKey? _key;
    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <summary>
    /// Returns a token valid for thirty minutes. The same token is returned until five minutes before it expires.
    /// </summary>
    /// <exception cref="BankProviderException">The application id or the private key is missing or cannot be loaded.</exception>
    public string Create()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();

            if (_token is not null && now < _expiresAt - RenewBeforeExpiry)
            {
                return _token;
            }

            var key = LoadKey();
            var expires = now + Lifetime;

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = "enablebanking.com",
                Audience = "api.enablebanking.com",
                IssuedAt = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            };

            _token = _handler.CreateToken(descriptor);
            _expiresAt = expires;
            return _token;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _rsa?.Dispose();
            _rsa = null;
            _key = null;
            _token = null;
        }
    }

    private RsaSecurityKey LoadKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ApplicationId))
        {
            throw KeyUnavailable($"{EnableBankingOptions.SectionName}:{nameof(EnableBankingOptions.ApplicationId)}");
        }

        var keyConfigurationName = $"{EnableBankingOptions.SectionName}:{nameof(EnableBankingOptions.PrivateKeyPath)}";

        if (string.IsNullOrWhiteSpace(settings.PrivateKeyPath))
        {
            throw KeyUnavailable(keyConfigurationName);
        }

        var rsa = RSA.Create();

        try
        {
            var pem = File.ReadAllText(settings.PrivateKeyPath);

            if (string.IsNullOrEmpty(settings.PrivateKeyPassword))
            {
                rsa.ImportFromPem(pem);
            }
            else
            {
                rsa.ImportFromEncryptedPem(pem, settings.PrivateKeyPassword);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            throw KeyUnavailable(keyConfigurationName);
        }

        _rsa = rsa;
        _key = new RsaSecurityKey(rsa) { KeyId = settings.ApplicationId };
        return _key;
    }

    private static BankProviderException KeyUnavailable(string configurationKey)
    {
        return new BankProviderException(
            ProviderErrorKind.ProviderAuth,
            "key_unavailable",
            $"The configuration key {configurationKey} is missing or cannot be used.");
    }
}
