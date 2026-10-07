using System.Threading.Channels;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>A sync to run in the background for one connection, carrying the details of the person who asked for it.</summary>
public record SyncRequest(Guid ConnectionId, SyncTrigger Trigger, FetchContext Context);

/// <summary>What happened to a request to queue a sync.</summary>
public enum EnqueueResult
{
    /// <summary>The request was queued.</summary>
    Queued,

    /// <summary>An identical request for the same connection is already waiting, so nothing was added.</summary>
    AlreadyQueued,

    /// <summary>The queue is full and the request was not queued.</summary>
    Full
}

/// <summary>Queues operator-triggered syncs so a request never waits for the bank.</summary>
public interface ISyncDispatcher
{
    /// <summary>
    /// Queues the request, unless an identical one (same connection and trigger) is already waiting or being run, or the queue
    /// is full. A request is never dropped silently: the result says what happened to it.
    /// </summary>
    EnqueueResult TryEnqueue(SyncRequest request);
}

/// <summary>
/// A bounded in-memory queue of sync requests, read by <see cref="SyncWorker"/>. Requests are collapsed per connection and
/// trigger from the moment they are queued until the worker has finished them, so repeated calls never multiply fetches.
/// </summary>
public sealed class ChannelSyncDispatcher : ISyncDispatcher
{
    /// <summary>The most requests that can wait at one time.</summary>
    public const int Capacity = 16;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid ConnectionId, SyncTrigger Trigger), byte> _pending = new();

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
    public EnqueueResult TryEnqueue(SyncRequest request)
    {
        if (!_pending.TryAdd((request.ConnectionId, request.Trigger), 0))
        {
            return EnqueueResult.AlreadyQueued;
        }

        if (_channel.Writer.TryWrite(request))
        {
            return EnqueueResult.Queued;
        }

        _pending.TryRemove((request.ConnectionId, request.Trigger), out _);
        return EnqueueResult.Full;
    }

    /// <summary>Marks a request as finished, so an identical one can be queued again.</summary>
    public void Complete(SyncRequest request)
    {
        _pending.TryRemove((request.ConnectionId, request.Trigger), out _);
    }
}

/// <summary>
/// Runs queued syncs one at a time, each in its own dependency scope. A sync that follows a link or renewal is the one fetch
/// that must not be skipped, so when the connection is busy with another run it waits and tries again instead of giving up.
/// </summary>
public class SyncWorker(
    ChannelSyncDispatcher dispatcher,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SyncWorker> logger) : BackgroundService
{
    /// <summary>How many times a post-link sync is retried while another run of the connection is still going.</summary>
    public const int BusyRetries = 30;

    /// <summary>How long to wait between those retries.</summary>
    public static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in dispatcher.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RunAsync(request, stoppingToken);
                }
                finally
                {
                    dispatcher.Complete(request);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        var retries = 0;

        while (true)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();

                await orchestrator.SyncConnectionAsync(request.ConnectionId, request.Trigger, request.Context, cancellationToken);
                return;
            }
            catch (SyncAlreadyRunningException) when (request.Trigger == SyncTrigger.PostLink && retries < BusyRetries)
            {
                retries++;
                logger.LogInformation(
                    "The sync after a link or renewal of connection {ConnectionId} waits because another run is still going.",
                    request.ConnectionId);
                await Task.Delay(BusyRetryDelay, timeProvider, cancellationToken);
            }
            catch (SyncAlreadyRunningException)
            {
                logger.Log(
                    request.Trigger == SyncTrigger.PostLink ? LogLevel.Warning : LogLevel.Information,
                    "A queued {Trigger} sync for connection {ConnectionId} was skipped because one is already running.",
                    request.Trigger,
                    request.ConnectionId);
                return;
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
                return;
            }
        }
    }
}
