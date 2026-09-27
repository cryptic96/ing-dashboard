using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ledger.Domain.Auth;

/// <summary>Generates, strictly parses and hashes named per-client API key tokens.</summary>
public static partial class ApiKeyToken
{
    private const string Prefix = "ldg";

    [GeneratedRegex(@"\Aldg_(?<keyId>[0-9a-f]{16})_(?<secret>[A-Za-z0-9_-]{43})\z")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Generates a new key id, its one-time token, and the SHA-256 hash of the secret to store.
    /// A 256-bit random secret already makes offline guessing infeasible, so running it through a
    /// slow key-derivation function (as some guides recommend for lower-entropy, human-chosen
    /// passwords) would only add CPU cost to every request on this low-power host for no security
    /// benefit; a plain SHA-256 of the high-entropy secret is deliberately used instead.
    /// </summary>
    public static (string KeyId, string Token, byte[] SecretSha256) Generate()
    {
        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = $"{Prefix}_{keyId}_{secret}";
        var secretSha256 = HashSecret(secret);

        return (keyId, token, secretSha256);
    }

    /// <summary>Strictly parses a presented token, rejecting anything that does not match the exact expected shape.</summary>
    public static bool TryParse(string? token, out string keyId, out string secret)
    {
        keyId = string.Empty;
        secret = string.Empty;

        if (token is null)
        {
            return false;
        }

        var match = TokenPattern().Match(token);

        if (!match.Success)
        {
            return false;
        }

        keyId = match.Groups["keyId"].Value;
        secret = match.Groups["secret"].Value;
        return true;
    }

    /// <summary>Compares a presented secret against a stored hash in fixed time, returning true only for the original secret.</summary>
    public static bool Matches(string secret, byte[] storedSecretSha256)
    {
        var computed = HashSecret(secret);
        return CryptographicOperations.FixedTimeEquals(computed, storedSecretSha256);
    }

    private static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}
