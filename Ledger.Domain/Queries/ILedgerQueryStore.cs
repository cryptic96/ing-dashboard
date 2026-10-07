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

    /// <summary>
    /// Reads one page of transactions that match the filter, newest first by period date, then first-seen time, then internal id,
    /// from a single read-only snapshot. Dropped transactions appear nowhere and counterparty account numbers are masked.
    /// </summary>
    /// <param name="filter">What the search selects.</param>
    /// <param name="status">Whether to show booked transactions, pending ones or both.</param>
    /// <param name="after">The key of the last row of the previous page, or null for the first page.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="zone">The time zone whose calendar decides which day a transaction without a bank date belongs to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<SearchPage> SearchAsync(
        LedgerQueryFilter filter,
        SearchStatus status,
        SearchPosition? after,
        int limit,
        TimeZoneInfo zone,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the counterparty names that contain the text, case-insensitively, among booked transactions of synced accounts, with
    /// one entry per name and currency. Transfers between synced accounts are left out and counterparty account numbers are masked.
    /// </summary>
    /// <param name="text">The text to look for in counterparty names.</param>
    /// <param name="range">The days to cover, or null for all history.</param>
    /// <param name="accountKeys">Opaque account keys to narrow to; empty means every synced account.</param>
    /// <param name="zone">The time zone whose calendar decides which day a transaction without a bank date belongs to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<CounterpartyPage> FindCounterpartiesAsync(
        string text,
        DateRange? range,
        IReadOnlyList<string> accountKeys,
        TimeZoneInfo zone,
        CancellationToken cancellationToken);
}

/// <summary>
/// One counterparty name in one currency: how many booked transactions it has, the money out and in, the first and last day and up
/// to three of the counterparty's account numbers, already masked.
/// </summary>
public record CounterpartyData(
    string Name,
    string Currency,
    int Count,
    decimal MoneyOut,
    decimal MoneyIn,
    DateOnly FirstDate,
    DateOnly LastDate,
    IReadOnlyList<string> MaskedAccounts);

/// <summary>The counterparty names that matched, one row per name and currency, and how many of the requested account keys are synced accounts.</summary>
public record CounterpartyPage(IReadOnlyList<CounterpartyData> Rows, int AccountsInScope);

/// <summary>One transaction as a search shows it. The counterparty account is already masked.</summary>
public record SearchRowData(
    Guid Id,
    DateTimeOffset FirstSeenAt,
    DateOnly PeriodDate,
    DateOnly? BookingDate,
    string Status,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyAccountMasked,
    string? Description,
    string AccountKey,
    string? AccountName,
    bool InternalTransfer);

/// <summary>
/// One page of a search: at most the requested number of rows, how many transactions match in all, whether more rows follow this
/// page, and how many of the requested account keys are synced accounts.
/// </summary>
public record SearchPage(IReadOnlyList<SearchRowData> Rows, int MatchingTotal, bool HasMore, int AccountsInScope);

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
