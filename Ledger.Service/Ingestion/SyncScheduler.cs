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
    private static readonly TimeSpan UnselectedWarningAge = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, bool> _warnedConnections = new(StringComparer.Ordinal);
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

    /// <summary>Marks every run left unfinished by a stopped process as abandoned and returns how many there were.</summary>
    public async Task<int> AbandonOrphanedRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var runStore = scope.ServiceProvider.GetRequiredService<ISyncRunStore>();

        var abandoned = await runStore.AbandonUnfinishedAsync(timeProvider.GetUtcNow(), cancellationToken);

        if (abandoned > 0)
        {
            logger.LogWarning("{Count} sync run(s) left unfinished by a restart were marked abandoned.", abandoned);
        }

        return abandoned;
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
            await AbandonOrphanedRunsAsync(stoppingToken);
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
            var orchestrator = services.GetRequiredService<SyncOrchestrator>();

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

    private async Task WarnOnceIfLongHistoryMayBeLostAsync(
        ConnectionSummary connection,
        DateTimeOffset now,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (now - connection.AuthorizedAt <= UnselectedWarningAge || _warnedConnections.ContainsKey(connection.ConnectionKey))
        {
            return;
        }

        var hasSynced = await services.GetRequiredService<IBankConnectionStore>()
            .HasAnySyncRunAsync(connection.Id, cancellationToken);

        if (!hasSynced && _warnedConnections.TryAdd(connection.ConnectionKey, true))
        {
            logger.LogWarning(
                "Connection {ConnectionKey} was authorised more than {Minutes} minutes ago and still has no selected account or sync, so the bank may no longer return its full history. Select the accounts to sync.",
                connection.ConnectionKey,
                (int)UnselectedWarningAge.TotalMinutes);
        }
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
