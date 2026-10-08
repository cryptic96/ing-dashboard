using System.Security.Cryptography;
using System.Text;
using Ledger.Domain.Ingestion;

namespace Ledger.Domain.Queries;

/// <summary>
/// A short, stable reference to a counterparty name. Spellings that differ only in case or spacing share a reference, so Claude
/// can filter by exactly one counterparty without repeating its name.
/// </summary>
public static class CounterpartyRef
{
    private const string Prefix = "cp_";
    private const int Length = 12;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// Returns "cp_" followed by 12 lowercase base32 characters of the SHA-256 hash of the name in its comparison form, or null for
    /// a blank name.
    /// </summary>
    /// <param name="name">The counterparty name as the bank sent it.</param>
    public static string? For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(TextNormalizer.ForMatching(name)!));
        var builder = new StringBuilder(Prefix, Prefix.Length + Length);

        for (var index = 0; index < Length; index++)
        {
            var bitOffset = index * 5;
            var byteIndex = bitOffset / 8;
            var shift = bitOffset % 8;
            var window = (hash[byteIndex] << 8) | hash[byteIndex + 1];
            builder.Append(Alphabet[(window >> (11 - shift)) & 0x1F]);
        }

        return builder.ToString();
    }

    /// <summary>Returns whether the text has the shape of a reference this class produces.</summary>
    /// <param name="reference">The text to check.</param>
    public static bool IsWellFormed(string? reference)
    {
        if (reference is null || reference.Length != Prefix.Length + Length || !reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return reference[Prefix.Length..].All(character => Alphabet.Contains(character, StringComparison.Ordinal));
    }
}
