using Ledger.Domain.Ingestion;
using Ledger.Service.Ingestion;
using Prometheus;

namespace Ledger.Service.Metrics;

/// <summary>
/// Exposes bank consent, sync, call allowance, reconciliation and review-flag state on /metrics. Every value is a projection of
/// database state, and every label is an opaque connection or account key or a fixed reason or state word, never a bank
/// identifier, account name or counterparty.
/// </summary>
public static class SyncMetrics
{
    private static readonly Lock Gate = new();

    private static readonly Gauge ConsentDays = Prometheus.Metrics.CreateGauge(
        "ledger_bank_consent_days_until_expiry",
        "Days until the bank consent of the connection ends; negative once it has ended.",
        "connection");

    private static readonly Gauge ConsentStateGauge = Prometheus.Metrics.CreateGauge(
        "ledger_bank_consent_state",
        "1 for the current consent state of the connection (linked, expiring or expired) and 0 for the others.",
        "connection", "state");

    private static readonly Gauge LastSuccess = Prometheus.Metrics.CreateGauge(
        "ledger_sync_last_success_timestamp_seconds",
        "Unix time of the last successful sync of the account on its current connection.",
        "account");

    private static readonly Gauge Failing = Prometheus.Metrics.CreateGauge(
        "ledger_sync_failing",
        "1 when the connection's latest finished sync counts as failed for the reason, 0 otherwise.",
        "connection", "reason");

    private static readonly Counter Errors = Prometheus.Metrics.CreateCounter(
        "ledger_sync_errors_total",
        "Finished sync runs that failed, by reason, counted over all history.",
        "reason");

    private static readonly Gauge CallsRemaining = Prometheus.Metrics.CreateGauge(
        "ledger_sync_calls_remaining",
        "Background bank calls the account may still make in the current allowance window.",
        "account");

    private static readonly Gauge ReconciliationDrift = Prometheus.Metrics.CreateGauge(
        "ledger_balance_reconciliation_drift",
        "1 when the account's latest reconciled balance did not match its transactions, 0 otherwise.",
        "account");

    private static readonly Gauge Flagged = Prometheus.Metrics.CreateGauge(
        "ledger_transactions_flagged",
        "Pending transactions of the account whose matching was ambiguous and need a person to look.",
        "account");

    private static readonly Dictionary<Gauge, Dictionary<string, string[]>> Published = [];

    /// <summary>Creates the error counter at zero for every failure reason, so a reason that never happened is still present.</summary>
    public static void InitialiseCounters()
    {
        foreach (var reason in SyncHealth.Reasons)
        {
            Errors.WithLabels(reason);
        }
    }

    /// <summary>
    /// Sets every gauge from the status snapshot, raises the error counter to the database count, and removes the series of
    /// connections and accounts that are no longer present.
    /// </summary>
    /// <param name="status">The database state to project.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="options">The ingestion settings that define the schedule and the call allowance.</param>
    /// <param name="zone">The time zone that defines the day.</param>
    public static void Apply(IngestionStatus status, DateTimeOffset now, IngestionOptions options, TimeZoneInfo zone)
    {
        var settings = new ScheduleSettings(options.ParseScheduleLocalTime(), zone, TimeSpan.FromHours(options.RetryDelayHours));

        var consentDays = new List<(string[] Labels, double Value)>();
        var consentStates = new List<(string[] Labels, double Value)>();
        var failing = new List<(string[] Labels, double Value)>();

        foreach (var connection in status.Connections)
        {
            var snapshot = ConsentState.Derive(connection.Status, connection.ValidUntil, now);
            var currentState = StateWord(snapshot.State);
            var currentReason = SyncHealth.FailingReason(connection.LatestFinishedRun, now, settings);

            consentDays.Add(([connection.ConnectionKey], snapshot.DaysUntilExpiry));

            foreach (var state in new[] { "linked", "expiring", "expired" })
            {
                consentStates.Add(([connection.ConnectionKey, state], state == currentState ? 1 : 0));
            }

            foreach (var reason in SyncHealth.Reasons)
            {
                failing.Add(([connection.ConnectionKey, reason], reason == currentReason ? 1 : 0));
            }
        }

        var lastSuccess = new List<(string[] Labels, double Value)>();
        var callsRemaining = new List<(string[] Labels, double Value)>();
        var drift = new List<(string[] Labels, double Value)>();
        var flagged = new List<(string[] Labels, double Value)>();

        foreach (var account in status.Accounts)
        {
            if (account.LastSuccessAt is { } succeededAt)
            {
                lastSuccess.Add(([account.AccountKey], succeededAt.ToUnixTimeMilliseconds() / 1000.0));
            }

            callsRemaining.Add((
                [account.AccountKey],
                CallBudget.Remaining(options.BackgroundCallsPerDay, account.BackgroundCallTimes, now, options.QuotaWindow, zone)));
            drift.Add(([account.AccountKey], account.LatestReconciled == false ? 1 : 0));
            flagged.Add(([account.AccountKey], account.FlaggedCount));
        }

        lock (Gate)
        {
            Publish(ConsentDays, consentDays);
            Publish(ConsentStateGauge, consentStates);
            Publish(Failing, failing);
            Publish(LastSuccess, lastSuccess);
            Publish(CallsRemaining, callsRemaining);
            Publish(ReconciliationDrift, drift);
            Publish(Flagged, flagged);

            foreach (var (reason, count) in status.FailedRunsByReason)
            {
                Errors.WithLabels(reason).IncTo(count);
            }
        }
    }

    private static string StateWord(ConsentView view)
    {
        return view switch
        {
            ConsentView.Linked => "linked",
            ConsentView.Expiring => "expiring",
            _ => "expired"
        };
    }

    private static void Publish(Gauge gauge, List<(string[] Labels, double Value)> series)
    {
        if (!Published.TryGetValue(gauge, out var previous))
        {
            previous = [];
            Published[gauge] = previous;
        }

        var current = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (labels, value) in series)
        {
            gauge.WithLabels(labels).Set(value);
            current[string.Join('\u001f', labels)] = labels;
        }

        foreach (var (key, labels) in previous)
        {
            if (!current.ContainsKey(key))
            {
                gauge.RemoveLabelled(labels);
            }
        }

        Published[gauge] = current;
    }
}
