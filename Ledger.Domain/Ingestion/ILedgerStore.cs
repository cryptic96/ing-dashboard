namespace Ledger.Domain.Ingestion;

/// <summary>Reads and writes the ledger's transactions for one account at a time.</summary>
public interface ILedgerStore
{
    /// <summary>Summarises what the account already holds, so the next fetch can choose how far back to reach.</summary>
    Task<FetchWindow> GetFetchWindowAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the account's transactions for reconciliation: every pending row whatever its date, plus every other row whose
    /// effective date is on or after the given date, or all rows when no date is given.
    /// </summary>
    Task<IReadOnlyList<LedgerTransactionState>> LoadStateAsync(Guid accountId, DateOnly? from, CancellationToken cancellationToken);

    /// <summary>Applies a plan in one database transaction: either every change lands or none does.</summary>
    Task<ApplyResult> ApplyAsync(
        Guid accountId,
        Guid syncRunId,
        ReconciliationPlan plan,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

/// <summary>What an account already holds: whether it has transactions, its latest effective date and its oldest pending date.</summary>
public record FetchWindow(bool HasTransactions, DateOnly? LatestEffectiveDate, DateOnly? OldestPendingDate);

/// <summary>How many rows a plan inserted, updated, merged, flagged and dropped.</summary>
public record ApplyResult(int Inserted, int Updated, int Merged, int Flagged, int Dropped);
