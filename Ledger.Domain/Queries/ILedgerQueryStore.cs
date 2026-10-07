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
