namespace Ledger.Domain.Ingestion;

/// <summary>Reads, in one snapshot, the state of the bank connections and selected accounts that operational metrics are derived from.</summary>
public interface IIngestionStatusStore
{
    /// <summary>
    /// Reads every active or provider-expired connection with its latest finished run, every selected account of those
    /// connections with its last success, recent background calls, latest reconciliation result and flagged count, and how many
    /// runs have failed for each reason over all history.
    /// </summary>
    /// <param name="now">The current instant, which bounds how far back background calls are read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IngestionStatus> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>A snapshot of ingestion state. Connections and accounts are identified only by their opaque keys.</summary>
public record IngestionStatus(
    IReadOnlyList<ConnectionHealth> Connections,
    IReadOnlyList<AccountHealth> Accounts,
    IReadOnlyDictionary<string, long> FailedRunsByReason);

/// <summary>A connection's consent and its most recent finished run, or null when it has not finished one.</summary>
public record ConnectionHealth(
    string ConnectionKey,
    ConnectionStatus Status,
    DateTimeOffset ValidUntil,
    SyncRunSummary? LatestFinishedRun);

/// <summary>
/// A selected account's sync state. The last success is that of its current connection and is null before the first success.
/// The reconciliation result is null while no balance has been checked. The flagged count is the pending transactions whose
/// matching was ambiguous.
/// </summary>
public record AccountHealth(
    string AccountKey,
    string ConnectionKey,
    DateTimeOffset? LastSuccessAt,
    IReadOnlyList<DateTimeOffset> BackgroundCallTimes,
    bool? LatestReconciled,
    int FlaggedCount);
