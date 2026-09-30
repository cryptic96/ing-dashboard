namespace Ledger.Domain.Ingestion;

/// <summary>Decides whether a connection's most recent finished sync counts as a failure worth alerting on, and why.</summary>
public static class SyncHealth
{
    /// <summary>A temporary failure that the day's retry did not fix, or that no retry can follow.</summary>
    public const string Transient = "transient";

    /// <summary>The provider or bank refused because a call allowance was used up.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The bank rejected the consent.</summary>
    public const string ConsentRejected = "consent_rejected";

    /// <summary>The provider rejected the application's credentials.</summary>
    public const string ProviderAuth = "provider_auth";

    /// <summary>How long after the planned retry instant the retry is still given the chance to finish before the failure counts.</summary>
    public static readonly TimeSpan RetryGrace = TimeSpan.FromMinutes(10);

    /// <summary>Every failure reason, in a fixed order.</summary>
    public static IReadOnlyList<string> Reasons { get; } = [Transient, RateLimited, ConsentRejected, ProviderAuth];

    /// <summary>Returns the failure reason a finished run's outcome belongs to, or null for a success.</summary>
    /// <param name="outcome">How the run ended.</param>
    public static string? ReasonForOutcome(SyncOutcome outcome)
    {
        return outcome switch
        {
            SyncOutcome.Succeeded => null,
            SyncOutcome.FailedRateLimited or SyncOutcome.QuotaExhausted => RateLimited,
            SyncOutcome.FailedConsent => ConsentRejected,
            SyncOutcome.FailedProviderAuth => ProviderAuth,
            _ => Transient
        };
    }

    /// <summary>
    /// Returns why the connection is failing, or null when it is not. A rate limit and a consent or credential rejection count at
    /// once. A temporary failure counts only when no automatic retry can still follow: the failed run was itself a retry or an
    /// attended run, the retry would fall on the next local day, or the retry instant plus a short grace has passed.
    /// </summary>
    /// <param name="latestFinished">The connection's most recent finished run, or null when it has none.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="settings">The schedule settings, which give the retry delay and the zone that defines the day.</param>
    public static string? FailingReason(SyncRunSummary? latestFinished, DateTimeOffset now, ScheduleSettings settings)
    {
        if (latestFinished is not { FinishedAt: { } finishedAt, Outcome: { } outcome })
        {
            return null;
        }

        var reason = ReasonForOutcome(outcome);

        if (reason != Transient)
        {
            return reason;
        }

        if (latestFinished.Trigger != SyncTrigger.Scheduled)
        {
            return Transient;
        }

        var retryAt = finishedAt + settings.RetryDelay;

        if (SyncSchedule.LocalDate(retryAt, settings.Zone) != SyncSchedule.LocalDate(finishedAt, settings.Zone))
        {
            return Transient;
        }

        return now < retryAt + RetryGrace ? null : Transient;
    }
}
