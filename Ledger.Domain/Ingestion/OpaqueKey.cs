using System.Security.Cryptography;

namespace Ledger.Domain.Ingestion;

/// <summary>Creates the short random keys used to name accounts and connections in metrics and views without exposing any bank identifier.</summary>
public static class OpaqueKey
{
    private const int ByteCount = 8;

    /// <summary>Returns 16 lowercase hexadecimal characters drawn from a cryptographic random source.</summary>
    public static string New()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(ByteCount));
    }
}
