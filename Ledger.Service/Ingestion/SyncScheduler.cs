using System.Collections.Concurrent;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Starts the daily background sync of every active connection that has a selected account. It looks at the persisted runs once
/// a minute, so a restart never loses or repeats the day's work, and it never sends the operator's presence to the bank.
/// </summary>
public class SyncScheduler(
    IServiceScopeFactory scopeFactory,
    IBankDataProvider provider,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnselectedFirstWarningAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnselectedSecondWarningAge = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan PostLinkRecoveryAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PostLinkRecoveryWindow = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<(string ConnectionKey, int Stage), bool> _warnedConnections = new();
    private ScheduleSettings? _settings;

    /// <summary>Starts every sync that is due at the given instant and returns how many runs were started.</summary>
    /// <param name="now">The instant to decide for.</param>
    /// <param name="cancellationToken">Stops the pass between connections.</param>
    public async Task<int> RunDueSyncsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = ResolveSettings();
        var started = 0;

        using var listScope = scopeFactory.CreateScope();
        var connections = await listScope.ServiceProvider
            .GetRequiredService<IBankConnectionStore>()
            .ListConnectionsAsync(cancellationToken);

        foreach (var connection in connections.Where(candidate => candidate.Status == ConnectionStatus.Active).OrderBy(candidate => candidate.AuthorizedAt))
        {
            if (ConsentState.Derive(connection.Status, connection.ValidUntil, now).State == ConsentView.Expired)
            {
                continue;
            }

            if (await RunConnectionAsync(connection, now, settings, cancellationToken))
            {
                started++;
            }
        }

        return started;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled || provider is DisabledBankDataProvider)
        {
            return;
        }

        try
        {
            ResolveSettings();
            await TickAsync(stoppingToken);

            using var timer = new PeriodicTimer(TickInterval, timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunDueSyncsAsync(timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("A scheduler pass failed with {ExceptionType}.", exception.GetType().Name);
        }
    }

    private async Task<bool> RunConnectionAsync(
        ConnectionSummary connection,
        DateTimeOffset now,
        ScheduleSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;

            var accounts = await services.GetRequiredService<IBankConnectionStore>()
                .ListAccountsAsync(connection.Id, cancellationToken);

            if (!accounts.Any(account => account.SyncEnabled))
            {
                await WarnOnceIfLongHistoryMayBeLostAsync(connection, now, services, cancellationToken);
                return false;
            }

            var orchestrator = services.GetRequiredService<SyncOrchestrator>();

            if (await NeedsPostLinkRecoveryAsync(connection, now, services, cancellationToken))
            {
                logger.LogWarning(
                    "Connection {ConnectionKey} has selected accounts but never synced, so its first sync is started now as a recovery.",
                    connection.ConnectionKey);
                await orchestrator.SyncConnectionAsync(connection.Id, SyncTrigger.PostLink, FetchContext.Background, cancellationToken);
                return true;
            }

            var runStore = services.GetRequiredService<ISyncRunStore>();
            var runs = await runStore.ListRunsSinceAsync(
                connection.Id,
                SyncSchedule.StartOfLocalDay(now, settings.Zone),
                cancellationToken);

            var decision = SyncSchedule.Decide(now, settings, runs);

            if (decision == SyncDecisionKind.None)
            {
                return false;
            }

            var trigger = decision == SyncDecisionKind.Retry ? SyncTrigger.Retry : SyncTrigger.Scheduled;

            await orchestrator.SyncConnectionAsync(connection.Id, trigger, FetchContext.Background, cancellationToken);
            return true;
        }
        catch (SyncAlreadyRunningException)
        {
            logger.LogInformation("The scheduled sync of connection {ConnectionKey} was skipped because one is already running.", connection.ConnectionKey);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "The scheduled sync of connection {ConnectionKey} failed with {ExceptionType}.",
                connection.ConnectionKey,
                exception.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Whether a connection that was approved a few minutes ago, has selected accounts and has never synced needs its first sync
    /// started here. The queued request for it lives in memory only, so a restart in between would lose it, and the first sync
    /// must read the full history while the bank still returns it. Beyond the window the daily schedule takes over.
    /// </summary>
    private static async Task<bool> NeedsPostLinkRecoveryAsync(
        ConnectionSummary connection,
        DateTimeOffset now,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var age = now - connection.AuthorizedAt;

        if (age < PostLinkRecoveryAge || age > PostLinkRecoveryWindow)
        {
            return false;
        }

        return !await services.GetRequiredService<IBankConnectionStore>().HasAnySyncRunAsync(connection.Id, cancellationToken);
    }

    /// <summary>
    /// Warns, early and again later, about a connection that was approved but has no selected account and never synced. The bank
    /// returns the full history only for about an hour after approval, so the first warning comes within minutes while there is
    /// still time to act, and the second says the window has probably closed and the connection must be renewed for a full read.
    /// </summary>
    private async Task WarnOnceIfLongHistoryMayBeLostAsync(
        ConnectionSummary connection,
        DateTimeOffset now,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var age = now - connection.AuthorizedAt;
        var stage = age > UnselectedSecondWarningAge ? 2 : age > UnselectedFirstWarningAge ? 1 : 0;

        if (stage == 0 || _warnedConnections.ContainsKey((connection.ConnectionKey, stage)))
        {
            return;
        }

        var hasSynced = await services.GetRequiredService<IBankConnectionStore>()
            .HasAnySyncRunAsync(connection.Id, cancellationToken);

        if (hasSynced || !_warnedConnections.TryAdd((connection.ConnectionKey, stage), true))
        {
            return;
        }

        if (stage == 1)
        {
            logger.LogWarning(
                "Connection {ConnectionKey} was authorised {Minutes} minutes ago and has no selected account or sync yet. Select the accounts to sync now: the bank returns the full history only for about an hour after approval.",
                connection.ConnectionKey,
                (int)UnselectedFirstWarningAge.TotalMinutes);
            return;
        }

        logger.LogWarning(
            "Connection {ConnectionKey} was authorised more than {Minutes} minutes ago and still has no selected account or sync, so the bank probably no longer returns its full history. Select the accounts and renew the connection to read the full history.",
            connection.ConnectionKey,
            (int)UnselectedSecondWarningAge.TotalMinutes);
    }

    private ScheduleSettings ResolveSettings()
    {
        if (_settings is { } existing)
        {
            return existing;
        }

        var current = options.Value;
        var settings = new ScheduleSettings(
            current.ParseScheduleLocalTime(),
            current.ResolveTimeZone(),
            TimeSpan.FromHours(current.RetryDelayHours));

        _settings = settings;
        return settings;
    }
}
