using Ledger.Domain.Ingestion;

namespace Ledger.Repository.Entities;

/// <summary>One attempt to sync a connection. At most one run per connection can be unfinished at a time.</summary>
public class SyncRunEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The connection being synced.</summary>
    public Guid BankConnectionId { get; set; }

    /// <summary>What started the run.</summary>
    public SyncTrigger Trigger { get; set; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the run ended; null while it is unfinished.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>How the run ended; null while it is unfinished.</summary>
    public SyncOutcome? Outcome { get; set; }

    /// <summary>A short provider error code for a failed run, at most 64 characters.</summary>
    public string? ProviderError { get; set; }

    /// <summary>How many provider calls the run made.</summary>
    public int CallsMade { get; set; }

    /// <summary>How many transactions the run inserted.</summary>
    public int Inserted { get; set; }

    /// <summary>How many transactions the run updated.</summary>
    public int Updated { get; set; }

    /// <summary>How many transactions the run dropped.</summary>
    public int Dropped { get; set; }

    /// <summary>How many transactions the run flagged for review.</summary>
    public int Flagged { get; set; }
}
