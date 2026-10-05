using Ledger.Domain.Ingestion;
using Ledger.Service.Metrics;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Keeps the sync and consent metrics in step with the database: it reads the ingestion state at startup and then every minute,
/// so a restart never resets the last success time or hides a failing state.
/// </summary>
public class SyncMetricsRefresher(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<SyncMetricsRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Reads the database once and applies it to the metrics. Concurrent calls run one after the other.</summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken);

        try
        {
            var current = options.Value;
            var now = timeProvider.GetUtcNow();

            using var scope = scopeFactory.CreateScope();
            var status = await scope.ServiceProvider
                .GetRequiredService<IIngestionStatusStore>()
                .ReadAsync(now, cancellationToken);

            SyncMetrics.Apply(status, now, current, current.ResolveTimeZone());
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SyncMetrics.InitialiseCounters();

        try
        {
            await TryRefreshAsync(stoppingToken);

            using var timer = new PeriodicTimer(RefreshInterval, timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TryRefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshOnceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Refreshing the sync metrics failed with {ExceptionType}; the previous values stay.", exception.GetType().Name);
        }
    }
}
