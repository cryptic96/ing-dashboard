namespace Ledger.Domain.Ingestion;

/// <summary>The lifecycle status of a ledger transaction.</summary>
public enum LedgerTransactionStatus
{
    /// <summary>Announced by the bank but not yet booked.</summary>
    Pending,

    /// <summary>Booked by the bank.</summary>
    Booked,

    /// <summary>A pending transaction the bank stopped reporting; kept for history, excluded from reporting.</summary>
    Dropped
}

/// <summary>A marker set when automatic matching could not decide and a person should look.</summary>
public enum MatchFlag
{
    /// <summary>No flag.</summary>
    None,

    /// <summary>More than one candidate matched, so nothing was merged.</summary>
    Ambiguous
}

/// <summary>What started a sync run.</summary>
public enum SyncTrigger
{
    /// <summary>The daily schedule.</summary>
    Scheduled,

    /// <summary>The single same-day retry after a transient failure.</summary>
    Retry,

    /// <summary>The first sync right after a consent was linked.</summary>
    PostLink,

    /// <summary>An operator asked for a sync.</summary>
    Manual
}

/// <summary>How a sync run ended.</summary>
public enum SyncOutcome
{
    /// <summary>Every selected account synced.</summary>
    Succeeded,

    /// <summary>A temporary failure; a retry may succeed.</summary>
    FailedTransient,

    /// <summary>The provider or bank refused because a call allowance was used up.</summary>
    FailedRateLimited,

    /// <summary>The consent was rejected, revoked or expired.</summary>
    FailedConsent,

    /// <summary>The application's credentials were refused by the provider.</summary>
    FailedProviderAuth,

    /// <summary>The provider returned data that cannot be stored faithfully.</summary>
    FailedMalformed,

    /// <summary>The daily call allowance did not permit the run.</summary>
    QuotaExhausted,

    /// <summary>The run never finished because the process stopped.</summary>
    Abandoned
}

/// <summary>
/// The stored state of one ledger transaction as the reconciler sees it: its internal id, every provider reference it was
/// ever seen under, and the fields matching relies on.
/// </summary>
public record LedgerTransactionState(
    Guid Id,
    LedgerTransactionStatus Status,
    IReadOnlyList<string> Refs,
    decimal Amount,
    string Currency,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    DateOnly? TransactionDate,
    string? CounterpartyName,
    string? Description,
    MatchFlag Flag,
    DateTimeOffset FirstSeenAt);
