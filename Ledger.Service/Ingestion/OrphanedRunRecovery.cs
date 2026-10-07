using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Marks every sync run left unfinished by a stopped process as abandoned, so a crash in the middle of a sync never blocks the
/// connection. It runs at every start whatever the scheduler setting, because operator-triggered syncs work without the
/// scheduler and need the same guarantee. It never stops the host: when the database cannot be reached it logs and tries again
/// until it succeeds, and it only ever touches runs that started before this process did.
/// </summary>
public class OrphanedRunRecovery(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OrphanedRunRecovery> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan SteadyRetryDelay = TimeSpan.FromMinutes(1);

    private DateTimeOffset _processStartedAt;
    private bool _recovered;

    /// <summary>
    /// Marks every run that started at or before the given instant and never finished as abandoned, and returns how many there were.
    /// </summary>
    /// <param name="startedAtOrBefore">The latest start of a run that can belong to a stopped process.</param>
    /// <param name="cancellationToken">Cancels the database call.</param>
    public async Task<int> AbandonAsync(DateTimeOffset startedAtOrBefore, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var runStore = scope.ServiceProvider.GetRequiredService<ISyncRunStore>();

        var abandoned = await runStore.AbandonUnfinishedAsync(timeProvider.GetUtcNow(), startedAtOrBefore, cancellationToken);

        if (abandoned > 0)
        {
            logger.LogWarning("{Count} sync run(s) left unfinished by a restart were marked abandoned.", abandoned);
        }

        return abandoned;
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _processStartedAt = timeProvider.GetUtcNow();
        _recovered = await TryAbandonAsync(cancellationToken);

        await base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        try
        {
            while (!_recovered)
            {
                var delay = attempt < RetryDelays.Length ? RetryDelays[attempt] : SteadyRetryDelay;
                attempt++;

                await Task.Delay(delay, timeProvider, stoppingToken);
                _recovered = await TryAbandonAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryAbandonAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AbandonAsync(_processStartedAt, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Marking sync runs left unfinished by a restart as abandoned failed with {ExceptionType}. It will be tried again.",
                exception.GetType().Name);
            return false;
        }
    }
}
