namespace Ledger.Domain.Queries;

/// <summary>Reads the ledger for questions asked through Claude. Results are plain records and never carry a bank identifier.</summary>
public interface ILedgerQueryStore
{
    /// <summary>
    /// Reads every account that is synced, in the order the accounts were created, with how far its booked history reaches,
    /// how many items are pending and its latest balance.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<OverviewAccountData>> ReadOverviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads everything one totals question needs from a single read-only snapshot: the booked sums, the pending transactions and
    /// the transfers between synced accounts that were left out, and the accounts in scope with their last successful sync.
    /// Dropped transactions appear nowhere.
    /// </summary>
    /// <param name="filter">What the question selects.</param>
    /// <param name="zone">The time zone whose calendar decides which day a transaction without a bank date belongs to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<TotalsData> ReadTotalsAsync(LedgerQueryFilter filter, TimeZoneInfo zone, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the distinct counterparty names of synced accounts whose reference is one of the given references, in ordinal order.
    /// </summary>
    /// <param name="refs">Counterparty references as produced by <see cref="CounterpartyRef"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<string>> ResolveCounterpartyNamesAsync(IReadOnlyList<string> refs, CancellationToken cancellationToken);
}

/// <summary>
/// A synced account as the overview shows it. The display name is null when the household never named the account. The
/// earliest and latest booked dates are null while the account holds no booked transaction.
/// </summary>
public record OverviewAccountData(
    string AccountKey,
    string? DisplayName,
    string Kind,
    string Currency,
    DateTimeOffset CreatedAt,
    DateOnly? EarliestBookedDate,
    DateOnly? LatestBookedDate,
    int PendingCount,
    OverviewBalanceData? LatestBalance);

/// <summary>A balance the bank reported: its amount, currency, kind and the date it applies to.</summary>
public record OverviewBalanceData(decimal Amount, string Currency, string Kind, DateOnly AppliesTo);
