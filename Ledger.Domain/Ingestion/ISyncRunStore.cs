namespace Ledger.Domain.Ingestion;

/// <summary>Records each sync run and guarantees at most one unfinished run per connection.</summary>
public interface ISyncRunStore
{
    /// <summary>Starts a run for the connection.</summary>
    /// <exception cref="SyncAlreadyRunningException">The connection already has an unfinished run.</exception>
    Task<Guid> StartAsync(Guid connectionId, SyncTrigger trigger, DateTimeOffset startedAt, CancellationToken cancellationToken);

    /// <summary>Records how the run ended.</summary>
    Task FinishAsync(Guid runId, SyncRunCompletion completion, CancellationToken cancellationToken);
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
