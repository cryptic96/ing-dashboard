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

    private static readonly TimeZoneInfo AmsterdamZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    /// <summary>
    /// Plans the changes for one account. Known references become updates and unknown references are matched against pending
    /// rows: a booked item that is certainly the booked version of exactly one pending row merges into it, a doubtful pairing
    /// is stored separately and the pending rows are flagged, and everything else is inserted. A pending item never
    /// downgrades a booked transaction, and items of any other status are ignored.
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

        var updates = new List<PlannedUpdate>();
        var resolved = new HashSet<Guid>();
        var unresolved = new List<(string Reference, ProviderTransaction Item)>();

        foreach (var (reference, item) in candidates)
        {
            if (item.Status is not (ProviderTransactionStatus.Pending or ProviderTransactionStatus.Booked))
            {
                continue;
            }

            if (!known.TryGetValue(reference, out var state))
            {
                unresolved.Add((reference, item));
                continue;
            }

            resolved.Add(state.Id);

            if (state.Status == LedgerTransactionStatus.Booked && item.Status == ProviderTransactionStatus.Pending)
            {
                continue;
            }

            var upgrade = state.Status == LedgerTransactionStatus.Pending && item.Status == ProviderTransactionStatus.Booked;
            updates.Add(new PlannedUpdate(state.Id, item, upgrade));
        }

        var matches = MatchUnresolvedBookedItems(existing, resolved, unresolved, options);

        var inserts = new List<PlannedInsert>();
        var merges = new List<PlannedMerge>();

        for (var index = 0; index < unresolved.Count; index++)
        {
            var (reference, item) = unresolved[index];

            if (matches.MergeTargets.TryGetValue(index, out var pendingId))
            {
                merges.Add(new PlannedMerge(pendingId, item, reference));
                continue;
            }

            inserts.Add(new PlannedInsert(item, reference, MatchFlag.None));
        }

        return new ReconciliationPlan(inserts, updates, merges, matches.Flagged, []);
    }

    private static MatchOutcome MatchUnresolvedBookedItems(
        IReadOnlyList<LedgerTransactionState> existing,
        HashSet<Guid> resolved,
        IReadOnlyList<(string Reference, ProviderTransaction Item)> unresolved,
        ReconcilerOptions options)
    {
        var pendingRows = existing
            .Where(state => state.Status == LedgerTransactionStatus.Pending && !resolved.Contains(state.Id))
            .ToList();

        var candidatesByItem = new Dictionary<int, List<LedgerTransactionState>>();
        var claims = new Dictionary<Guid, int>();

        for (var index = 0; index < unresolved.Count; index++)
        {
            var item = unresolved[index].Item;
            if (item.Status != ProviderTransactionStatus.Booked || ItemDate(item) is not { } itemDate)
            {
                continue;
            }

            var matching = pendingRows
                .Where(state => IsCandidate(state, item, itemDate, options.MatchWindowDays))
                .ToList();

            if (matching.Count == 0)
            {
                continue;
            }

            candidatesByItem[index] = matching;
            foreach (var state in matching)
            {
                claims[state.Id] = claims.GetValueOrDefault(state.Id) + 1;
            }
        }

        var mergeTargets = new Dictionary<int, Guid>();
        var flagged = new List<Guid>();

        foreach (var (index, matching) in candidatesByItem)
        {
            if (IsCertain(matching, unresolved[index].Item, claims))
            {
                mergeTargets[index] = matching[0].Id;
                continue;
            }

            foreach (var state in matching)
            {
                if (state.Flag != MatchFlag.Ambiguous && !flagged.Contains(state.Id))
                {
                    flagged.Add(state.Id);
                }
            }
        }

        return new MatchOutcome(mergeTargets, flagged);
    }

    private static bool IsCertain(
        IReadOnlyList<LedgerTransactionState> matching,
        ProviderTransaction item,
        IReadOnlyDictionary<Guid, int> claims)
    {
        if (matching.Count != 1 || claims[matching[0].Id] != 1)
        {
            return false;
        }

        var itemCounterparty = NormalisedCounterparty(item.CounterpartyName);
        return itemCounterparty is not null
            && itemCounterparty == NormalisedCounterparty(matching[0].CounterpartyName);
    }

    private static bool IsCandidate(LedgerTransactionState state, ProviderTransaction item, DateOnly itemDate, int windowDays)
    {
        if (state.Amount != item.Amount || !string.Equals(state.Currency, item.Currency, StringComparison.Ordinal))
        {
            return false;
        }

        if (Math.Abs(EffectiveDate(state).DayNumber - itemDate.DayNumber) > windowDays)
        {
            return false;
        }

        var stateCounterparty = NormalisedCounterparty(state.CounterpartyName);
        var itemCounterparty = NormalisedCounterparty(item.CounterpartyName);
        return stateCounterparty is null || itemCounterparty is null || stateCounterparty == itemCounterparty;
    }

    private static string? NormalisedCounterparty(string? name)
    {
        var normalised = TextNormalizer.ForMatching(name);
        return string.IsNullOrEmpty(normalised) ? null : normalised;
    }

    private static DateOnly EffectiveDate(LedgerTransactionState state)
    {
        return state.TransactionDate
            ?? state.BookingDate
            ?? state.ValueDate
            ?? SyncSchedule.LocalDate(state.FirstSeenAt, AmsterdamZone);
    }

    private static DateOnly? ItemDate(ProviderTransaction item)
    {
        return item.TransactionDate ?? item.BookingDate ?? item.ValueDate;
    }

    private sealed record MatchOutcome(IReadOnlyDictionary<int, Guid> MergeTargets, IReadOnlyList<Guid> Flagged);

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
