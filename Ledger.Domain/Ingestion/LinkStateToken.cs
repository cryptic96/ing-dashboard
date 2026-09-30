using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ledger.Domain.Ingestion;

/// <summary>
/// Creates and validates the one-time state that ties a bank redirect back to a link request started by an authenticated caller.
/// Only the SHA-256 of the state is ever stored.
/// </summary>
public static partial class LinkStateToken
{
    private const int ByteCount = 32;

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    private static partial Regex StatePattern();

    /// <summary>Generates a 256-bit random state as 43 unpadded base64url characters, with the SHA-256 of that text to store.</summary>
    public static (string State, byte[] Sha256) Generate()
    {
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteCount));
        return (state, SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    /// <summary>Hashes a presented state, refusing anything that does not have the exact shape a generated state has.</summary>
    public static bool TryHash(string? presented, out byte[] sha256)
    {
        sha256 = [];

        if (presented is null || !StatePattern().IsMatch(presented))
        {
            return false;
        }

        sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return true;
    }
}
