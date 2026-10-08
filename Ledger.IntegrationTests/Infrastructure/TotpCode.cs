using System.Globalization;
using System.Security.Cryptography;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Computes the six-digit one-time code an authenticator app shows (HMAC-SHA1, 30-second step), from the Base32 key Identity
/// stores for a login. Test code only; the host verifies codes through Identity.
/// </summary>
public static class TotpCode
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int StepSeconds = 30;

    /// <summary>The number of the 30-second time step that contains the moment.</summary>
    public static long StepOf(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code an authenticator holding the key shows at the given moment.</summary>
    public static string Compute(string base32Key, DateTimeOffset at)
    {
        var key = DecodeBase32(base32Key);
        var step = at.ToUnixTimeSeconds() / StepSeconds;

        var counter = new byte[8];
        for (var index = 7; index >= 0; index--)
        {
            counter[index] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>A code that is not valid at any step near the given moment.</summary>
    public static string Wrong(string base32Key, DateTimeOffset at)
    {
        var valid = Enumerable.Range(-4, 9)
            .Select(offset => Compute(base32Key, at.AddSeconds(offset * StepSeconds)))
            .ToHashSet(StringComparer.Ordinal);

        for (var candidate = 0; candidate < 1_000_000; candidate++)
        {
            var text = candidate.ToString("D6", CultureInfo.InvariantCulture);

            if (!valid.Contains(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException("Every six-digit code is valid.");
    }

    private static byte[] DecodeBase32(string text)
    {
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var character in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);

            if (value < 0)
            {
                continue;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. bytes];
    }
}
