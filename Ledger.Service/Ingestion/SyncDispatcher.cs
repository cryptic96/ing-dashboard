using System.Threading.Channels;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>A sync to run in the background for one connection, carrying the details of the person who asked for it.</summary>
public record SyncRequest(Guid ConnectionId, SyncTrigger Trigger, FetchContext Context);

/// <summary>Queues operator-triggered syncs so a request never waits for the bank.</summary>
public interface ISyncDispatcher
{
    /// <summary>Queues the request and returns true, or returns false without queueing when the queue is full.</summary>
    bool TryEnqueue(SyncRequest request);
}

/// <summary>A bounded in-memory queue of sync requests, read by <see cref="SyncWorker"/>.</summary>
public sealed class ChannelSyncDispatcher : ISyncDispatcher
{
    /// <summary>The most requests that can wait at one time.</summary>
    public const int Capacity = 16;

    private readonly Channel<SyncRequest> _channel = Channel.CreateBounded<SyncRequest>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>The reading side of the queue.</summary>
    public ChannelReader<SyncRequest> Reader => _channel.Reader;

    /// <inheritdoc />
    public bool TryEnqueue(SyncRequest request)
    {
        return _channel.Writer.TryWrite(request);
    }
}

/// <summary>Runs queued syncs one at a time, each in its own dependency scope.</summary>
public class SyncWorker(
    ChannelSyncDispatcher dispatcher,
    IServiceScopeFactory scopeFactory,
    ILogger<SyncWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in dispatcher.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();

            await orchestrator.SyncConnectionAsync(request.ConnectionId, request.Trigger, request.Context, cancellationToken);
        }
        catch (SyncAlreadyRunningException)
        {
            logger.LogInformation("A queued sync for connection {ConnectionId} was skipped because one is already running.", request.ConnectionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "A queued sync for connection {ConnectionId} failed with {ExceptionType}.",
                request.ConnectionId,
                exception.GetType().Name);
        }
    }
}
