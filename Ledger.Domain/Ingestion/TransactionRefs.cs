using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>Derives the stable provider reference under which each observed transaction is remembered.</summary>
public static class TransactionRefs
{
    private const char UnitSeparator = '\u001F';

    /// <summary>
    /// Returns one reference per item, in feed order. An item that carries an entry reference gets "er:" plus that reference.
    /// Otherwise it gets "fp:" plus a SHA-256 fingerprint of its date, signed amount, currency and normalised counterparty
    /// and description, followed by ":" and the zero-based occurrence index among identical fingerprints in the same feed,
    /// so two genuinely identical payments stay two transactions.
    /// </summary>
    public static IReadOnlyList<string> For(IReadOnlyList<ProviderTransaction> items)
    {
        var references = new List<string>(items.Count);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.EntryReference))
            {
                references.Add("er:" + item.EntryReference);
                continue;
            }

            var fingerprint = Fingerprint(item);
            occurrences.TryGetValue(fingerprint, out var seen);
            occurrences[fingerprint] = seen + 1;
            references.Add($"fp:{fingerprint}:{seen.ToString(CultureInfo.InvariantCulture)}");
        }

        return references;
    }

    private static string Fingerprint(ProviderTransaction item)
    {
        var date = item.BookingDate ?? item.TransactionDate;

        var joined = string.Join(
            UnitSeparator,
            date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            item.Amount.ToString("F4", CultureInfo.InvariantCulture),
            item.Currency,
            TextNormalizer.ForMatching(item.CounterpartyName) ?? string.Empty,
            TextNormalizer.ForMatching(item.Description) ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
