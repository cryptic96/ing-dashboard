using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Security;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Runs one sync for one connection: for each selected account it reads every page from the provider, plans the changes
/// against the ledger and applies them in a single database transaction. It stops at the first failure, so one failing
/// account never leaves another half-applied.
/// </summary>
public class SyncOrchestrator(
    IBankDataProvider provider,
    IBankConnectionStore connectionStore,
    ILedgerStore ledgerStore,
    ISyncRunStore runStore,
    ISecretProtector secretProtector,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<SyncOrchestrator> logger)
{
    /// <summary>Syncs every selected account of the connection and records the run.</summary>
    /// <exception cref="SyncAlreadyRunningException">The connection already has an unfinished run.</exception>
    public async Task<SyncRunResult> SyncConnectionAsync(
        Guid connectionId,
        SyncTrigger trigger,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        var runId = await runStore.StartAsync(connectionId, trigger, timeProvider.GetUtcNow(), cancellationToken);
        var progress = new RunProgress();
        var outcome = SyncOutcome.Succeeded;
        string? providerError = null;

        try
        {
            await SyncAccountsAsync(connectionId, runId, trigger, context, progress, cancellationToken);
        }
        catch (BankProviderException exception)
        {
            outcome = MapOutcome(exception.Kind);
            providerError = exception.ProviderCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishAsync(runId, SyncOutcome.Abandoned, null, progress, CancellationToken.None);
            LogRun(runId, SyncOutcome.Abandoned, progress);
            throw;
        }
        catch (Exception exception)
        {
            outcome = SyncOutcome.FailedTransient;
            providerError = exception.GetType().Name;
        }

        await FinishAsync(runId, outcome, providerError, progress, cancellationToken);
        LogRun(runId, outcome, progress);

        return new SyncRunResult(runId, outcome, progress.Inserted, progress.Updated, progress.Dropped, progress.Flagged);
    }

    private async Task SyncAccountsAsync(
        Guid connectionId,
        Guid runId,
        SyncTrigger trigger,
        FetchContext context,
        RunProgress progress,
        CancellationToken cancellationToken)
    {
        var target = await connectionStore.GetSyncTargetAsync(connectionId, cancellationToken)
            ?? throw new InvalidOperationException("The connection to sync does not exist.");

        var sessionId = secretProtector.Unprotect(target.ProtectedSessionId);

        foreach (var account in target.Accounts)
        {
            progress.AccountKeys.Add(account.AccountKey);
            await SyncAccountAsync(account, sessionId, runId, trigger, context, progress, cancellationToken);
        }
    }

    private async Task SyncAccountAsync(
        SyncAccount account,
        string sessionId,
        Guid runId,
        SyncTrigger trigger,
        FetchContext context,
        RunProgress progress,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var window = await ledgerStore.GetFetchWindowAsync(account.AccountId, cancellationToken);
        var query = ChooseQuery(trigger, window, settings.OverlapDays);

        var items = new List<ProviderTransaction>();
        var complete = false;
        var accountRef = new ProviderAccountRef(sessionId, account.ProviderAccountUid);

        await foreach (var page in provider.GetTransactionsAsync(accountRef, query, context, cancellationToken))
        {
            progress.CallsMade++;
            items.AddRange(page.Transactions);
        }

        complete = true;

        var stateFrom = query.DateFrom?.AddDays(-settings.MatchWindowDays);
        var existing = await ledgerStore.LoadStateAsync(account.AccountId, stateFrom, cancellationToken);

        var plan = TransactionReconciler.Plan(
            existing,
            items,
            new FetchCoverage(query.DateFrom, complete, items.Count),
            new ReconcilerOptions(settings.MatchWindowDays));

        var applied = await ledgerStore.ApplyAsync(
            account.AccountId,
            runId,
            plan,
            timeProvider.GetUtcNow(),
            cancellationToken);

        progress.Inserted += applied.Inserted;
        progress.Updated += applied.Updated + applied.Merged;
        progress.Dropped += applied.Dropped;
        progress.Flagged += applied.Flagged;
    }

    private static TransactionQuery ChooseQuery(SyncTrigger trigger, FetchWindow window, int overlapDays)
    {
        if (trigger == SyncTrigger.PostLink || !window.HasTransactions || window.LatestEffectiveDate is null)
        {
            return new TransactionQuery(null, HistoryDepth.Longest);
        }

        var from = window.LatestEffectiveDate.Value.AddDays(-Math.Max(0, overlapDays));

        if (window.OldestPendingDate is { } oldestPending && oldestPending < from)
        {
            from = oldestPending;
        }

        return new TransactionQuery(from, HistoryDepth.Incremental);
    }

    private static SyncOutcome MapOutcome(ProviderErrorKind kind)
    {
        return kind switch
        {
            ProviderErrorKind.Transient => SyncOutcome.FailedTransient,
            ProviderErrorKind.RateLimited => SyncOutcome.FailedRateLimited,
            ProviderErrorKind.ConsentRejected => SyncOutcome.FailedConsent,
            ProviderErrorKind.ProviderAuth => SyncOutcome.FailedProviderAuth,
            ProviderErrorKind.MalformedData => SyncOutcome.FailedMalformed,
            ProviderErrorKind.NotConfigured => SyncOutcome.FailedProviderAuth,
            _ => SyncOutcome.FailedTransient
        };
    }

    private Task FinishAsync(
        Guid runId,
        SyncOutcome outcome,
        string? providerError,
        RunProgress progress,
        CancellationToken cancellationToken)
    {
        return runStore.FinishAsync(
            runId,
            new SyncRunCompletion(
                outcome,
                providerError,
                progress.CallsMade,
                progress.Inserted,
                progress.Updated,
                progress.Dropped,
                progress.Flagged,
                timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private void LogRun(Guid runId, SyncOutcome outcome, RunProgress progress)
    {
        var level = outcome == SyncOutcome.Succeeded ? LogLevel.Information : LogLevel.Warning;
        logger.Log(
            level,
            "Sync run {RunId} finished with outcome {Outcome} for accounts {AccountKeys}.",
            runId,
            outcome,
            string.Join(",", progress.AccountKeys));
    }

    private sealed class RunProgress
    {
        public List<string> AccountKeys { get; } = [];

        public int CallsMade { get; set; }

        public int Inserted { get; set; }

        public int Updated { get; set; }

        public int Dropped { get; set; }

        public int Flagged { get; set; }
    }
}

/// <summary>The result of one sync run.</summary>
public record SyncRunResult(Guid RunId, SyncOutcome Outcome, int Inserted, int Updated, int Dropped, int Flagged);
