using System.Text.Json;
using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>
/// Compares a complete fetch of one account against what the ledger already holds and plans the changes. It is a pure
/// function: it reads nothing and writes nothing, so every scenario can be tested without a database.
/// </summary>
public static class TransactionReconciler
{
    private const int MaxAmountScale = 4;
    private const decimal AmountMagnitudeLimit = 1_000_000_000_000_000m;
    private const int CurrencyLength = 3;

    /// <summary>
    /// Plans the changes for one account. Known references become updates, unknown references become inserts, cancelled and
    /// other-status items are ignored, and a pending item never downgrades a booked transaction.
    /// </summary>
    /// <exception cref="BankProviderException">An item has an amount, currency or payload that cannot be stored faithfully.</exception>
    public static ReconciliationPlan Plan(
        IReadOnlyList<LedgerTransactionState> existing,
        IReadOnlyList<ProviderTransaction> incoming,
        FetchCoverage coverage,
        ReconcilerOptions options)
    {
        foreach (var item in incoming)
        {
            Validate(item);
        }

        var references = TransactionRefs.For(incoming);
        var known = IndexByReference(existing);
        var candidates = CollapseDuplicateReferences(incoming, references);

        var inserts = new List<PlannedInsert>();
        var updates = new List<PlannedUpdate>();

        foreach (var (reference, item) in candidates)
        {
            if (item.Status is not (ProviderTransactionStatus.Pending or ProviderTransactionStatus.Booked))
            {
                continue;
            }

            if (!known.TryGetValue(reference, out var state))
            {
                inserts.Add(new PlannedInsert(item, reference, MatchFlag.None));
                continue;
            }

            if (state.Status == LedgerTransactionStatus.Booked && item.Status == ProviderTransactionStatus.Pending)
            {
                continue;
            }

            var upgrade = state.Status == LedgerTransactionStatus.Pending && item.Status == ProviderTransactionStatus.Booked;
            updates.Add(new PlannedUpdate(state.Id, item, upgrade));
        }

        return new ReconciliationPlan(inserts, updates, [], [], []);
    }

    private static Dictionary<string, LedgerTransactionState> IndexByReference(IReadOnlyList<LedgerTransactionState> existing)
    {
        var index = new Dictionary<string, LedgerTransactionState>(StringComparer.Ordinal);

        foreach (var state in existing)
        {
            foreach (var reference in state.Refs)
            {
                index[reference] = state;
            }
        }

        return index;
    }

    private static List<(string Reference, ProviderTransaction Item)> CollapseDuplicateReferences(
        IReadOnlyList<ProviderTransaction> incoming,
        IReadOnlyList<string> references)
    {
        var ordered = new List<(string Reference, ProviderTransaction Item)>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < incoming.Count; i++)
        {
            var reference = references[i];
            var item = incoming[i];

            if (!positions.TryGetValue(reference, out var position))
            {
                positions[reference] = ordered.Count;
                ordered.Add((reference, item));
                continue;
            }

            var earlier = ordered[position].Item;
            if (item.Status == ProviderTransactionStatus.Booked || earlier.Status != ProviderTransactionStatus.Booked)
            {
                ordered[position] = (reference, item);
            }
        }

        return ordered;
    }

    private static void Validate(ProviderTransaction item)
    {
        if (item.Amount != decimal.Round(item.Amount, MaxAmountScale))
        {
            throw Malformed("amount_scale", "A transaction amount has more decimal places than the ledger stores.");
        }

        if (Math.Abs(item.Amount) >= AmountMagnitudeLimit)
        {
            throw Malformed("amount_magnitude", "A transaction amount is larger than the ledger stores.");
        }

        if (!IsCurrencyCode(item.Currency))
        {
            throw Malformed("currency", "A transaction has an invalid currency code.");
        }

        if (!IsJson(item.RawJson))
        {
            throw Malformed("payload", "A transaction payload is not valid JSON.");
        }
    }

    private static bool IsCurrencyCode(string? currency)
    {
        return currency is { Length: CurrencyLength } && currency.All(character => character is >= 'A' and <= 'Z');
    }

    private static bool IsJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static BankProviderException Malformed(string code, string message)
    {
        return new BankProviderException(ProviderErrorKind.MalformedData, code, message);
    }
}
