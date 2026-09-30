using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>Stores the daily balance snapshots and answers the questions reconciliation asks of the ledger. Snapshots are append-only.</summary>
public interface IBalanceStore
{
    /// <summary>Whether the account already has a snapshot for the given local date.</summary>
    Task<bool> HasSnapshotForDateAsync(Guid accountId, DateOnly localDate, CancellationToken cancellationToken);

    /// <summary>Returns the account's most recent snapshot of the given kind taken before the given local date, or null when there is none.</summary>
    Task<BalanceSnapshotState?> GetLatestBeforeAsync(
        Guid accountId,
        BalanceKind kind,
        DateOnly localDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sums the signed amounts of the account's booked transactions whose booking date is after the first date and on or before
    /// the second. Pending and dropped transactions never count. Returns zero when there are none.
    /// </summary>
    Task<decimal> SumBookedAsync(Guid accountId, DateOnly afterExclusive, DateOnly toInclusive, CancellationToken cancellationToken);

    /// <summary>
    /// Saves one snapshot row per returned balance kind for the local date. The check is attached to the row of the reconciled
    /// kind only; every other row records an unknown result.
    /// </summary>
    Task SaveAsync(
        Guid accountId,
        Guid syncRunId,
        DateOnly localDate,
        IReadOnlyList<ProviderBalance> balances,
        BalanceKind? reconciledKind,
        BalanceCheck? check,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);
}
