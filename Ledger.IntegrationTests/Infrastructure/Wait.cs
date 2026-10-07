using System.Diagnostics;
using System.Net;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Polls until something has happened and fails with a descriptive timeout instead of returning a half-finished state. Limits
/// are measured on a monotonic clock and are generous, because a slow machine must not turn into a misleading assertion later.
/// </summary>
public static class Wait
{
    /// <summary>How long to wait when the caller names no limit.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Reads until the condition holds and returns that last read; throws a <see cref="TimeoutException"/> naming what was waited for and the last observation.</summary>
    /// <param name="read">Reads the current state.</param>
    /// <param name="isDone">Whether the state is the one waited for.</param>
    /// <param name="description">What is waited for, in plain words.</param>
    /// <param name="describe">Renders the last observed state for the timeout message; counts and statuses only.</param>
    /// <param name="timeout">The limit; <see cref="DefaultTimeout"/> when null.</param>
    /// <param name="interval">The pause between reads; a hundred milliseconds when null.</param>
    public static async Task<T> UntilAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> isDone,
        string description,
        Func<T, string>? describe = null,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var current = await read();

            if (isDone(current))
            {
                return current;
            }

            if (stopwatch.Elapsed >= limit)
            {
                throw new TimeoutException(
                    $"Waited {limit.TotalSeconds:0} seconds for {description}; last observed: {(describe is null ? current?.ToString() : describe(current))}.");
            }

            await Task.Delay(interval ?? DefaultInterval, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until the ops endpoint of the host reports healthy.</summary>
    public static async Task UntilReadyAsync(LedgerWebApplicationFactory factory, TimeSpan? timeout = null)
    {
        using var opsClient = factory.CreateOpsClient();
        using var response = await ForHealthStatusAsync(opsClient, HttpStatusCode.OK, timeout);
    }

    /// <summary>Waits until the health endpoint answers with the given status and returns that response.</summary>
    public static async Task<HttpResponseMessage> ForHealthStatusAsync(HttpClient client, HttpStatusCode expected, TimeSpan? timeout = null)
    {
        HttpResponseMessage? kept = null;

        try
        {
            var response = await UntilAsync(
                async () =>
                {
                    kept?.Dispose();
                    kept = null;

                    try
                    {
                        kept = await client.GetAsync("/health", TestContext.Current.CancellationToken);
                        return (int?)kept.StatusCode;
                    }
                    catch (HttpRequestException)
                    {
                        return null;
                    }
                },
                status => status == (int)expected,
                $"the ops endpoint to answer {expected}",
                status => status is null ? "no answer" : $"status {status}",
                timeout,
                TimeSpan.FromMilliseconds(250));

            var result = kept!;
            kept = null;
            return result;
        }
        finally
        {
            kept?.Dispose();
        }
    }
}
