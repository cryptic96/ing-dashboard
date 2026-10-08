using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ledger.Domain.Auth;

/// <summary>
/// The one-time codes an authenticator app shows: their canonical spelling and the check of a code against the authenticator key.
/// The check reports which 30-second time step a code belongs to, so a caller can refuse every step at or below the last one it
/// accepted. Only the canonical spelling is accepted, so whatever is compared or claimed about a code is the same however the
/// person typed it.
/// </summary>
public static class TotpCodes
{
    /// <summary>How many digits a code has.</summary>
    public const int Digits = 6;

    /// <summary>How many seconds one time step lasts.</summary>
    public const int StepSeconds = 30;

    /// <summary>How many steps either side of the current one still verify, which allows for a clock that is slightly off.</summary>
    public const int StepTolerance = 2;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Whether the text is exactly six ASCII digits. A sign, whitespace, extra leading zeros and non-ASCII digits are all refused.
    /// </summary>
    public static bool IsWellFormed(string? code)
    {
        return code is { Length: Digits } && code.All(char.IsAsciiDigit);
    }

    /// <summary>The number of the 30-second time step that contains the moment.</summary>
    public static long TimeStepOf(DateTimeOffset moment)
    {
        return moment.ToUnixTimeSeconds() / StepSeconds;
    }

    /// <summary>
    /// Checks the code against the Base32 authenticator key at the given moment and returns the time step it belongs to, or null
    /// when the code is not well formed, the key cannot be read or no step within the tolerance produces it. Every step in the
    /// window is compared in constant time, and when several steps produce the same code the latest one is reported.
    /// </summary>
    public static long? MatchTimeStep(string? base32Key, string? code, DateTimeOffset now)
    {
        if (!IsWellFormed(code) || !TryDecodeBase32(base32Key, out var key))
        {
            return null;
        }

        var presented = Encoding.ASCII.GetBytes(code!);
        var current = TimeStepOf(now);
        long? matched = null;

        for (var offset = -StepTolerance; offset <= StepTolerance; offset++)
        {
            var step = current + offset;
            var expected = Encoding.ASCII.GetBytes(Compute(key, step));

            if (CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                matched = step;
            }
        }

        return matched;
    }

    private static string Compute(byte[] key, long step)
    {
        var counter = new byte[8];
        var remaining = step;

        for (var index = 7; index >= 0; index--)
        {
            counter[index] = (byte)(remaining & 0xFF);
            remaining >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static bool TryDecodeBase32(string? text, out byte[] bytes)
    {
        var decoded = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var character in (text ?? string.Empty).TrimEnd('=').ToUpperInvariant())
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);

            if (value < 0)
            {
                bytes = [];
                return false;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                decoded.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        bytes = [.. decoded];

        return bytes.Length > 0;
    }
}
