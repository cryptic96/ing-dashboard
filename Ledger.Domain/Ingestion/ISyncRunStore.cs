namespace Ledger.Domain.Ingestion;

/// <summary>Records each sync run and guarantees at most one unfinished run per connection.</summary>
public interface ISyncRunStore
{
    /// <summary>Starts a run for the connection.</summary>
    /// <exception cref="SyncAlreadyRunningException">The connection already has an unfinished run.</exception>
    Task<Guid> StartAsync(Guid connectionId, SyncTrigger trigger, DateTimeOffset startedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Records how the run ended, and only when it has not ended yet, so a run already finished or marked abandoned keeps its
    /// first outcome. Returns whether this call recorded the outcome.
    /// </summary>
    Task<bool> FinishAsync(Guid runId, SyncRunCompletion completion, CancellationToken cancellationToken);

    /// <summary>Lists the connection's runs that started at or after the given instant, in any order.</summary>
    Task<IReadOnlyList<SyncRunSummary>> ListRunsSinceAsync(Guid connectionId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Returns whether the connection has a run that has not finished.</summary>
    Task<bool> HasUnfinishedRunAsync(Guid connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks every unfinished run that started at or before the given instant as abandoned, because the process that owned it
    /// has stopped, and returns how many were marked. A run started after that instant belongs to a live process and is left alone.
    /// </summary>
    Task<int> AbandonUnfinishedAsync(DateTimeOffset now, DateTimeOffset startedAtOrBefore, CancellationToken cancellationToken);
}

/// <summary>How a run ended, with its call and row counts. The provider error is a short code, never a message with data.</summary>
public record SyncRunCompletion(
    SyncOutcome Outcome,
    string? ProviderError,
    int CallsMade,
    int Inserted,
    int Updated,
    int Dropped,
    int Flagged,
    DateTimeOffset FinishedAt);

/// <summary>Thrown when a run is started for a connection that already has an unfinished run.</summary>
public class SyncAlreadyRunningException(string message) : Exception(message);
