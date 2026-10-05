using System.Net;
using System.Text;

namespace Ledger.UnitTests.Ingestion.EnableBanking;

/// <summary>One request seen by the recording handler.</summary>
public record RecordedRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Returns the value of one query parameter, or null when it was not sent.</summary>
    public string? Parameter(string name)
    {
        return Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == name)
            .Select(pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty))
            .FirstOrDefault();
    }

    /// <summary>Returns the query string without the continuation key, which is the part that must stay identical across pages.</summary>
    public string FixedQuery()
    {
        return string.Join(
            '&',
            Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => !pair.StartsWith("continuation_key=", StringComparison.Ordinal)));
    }
}

/// <summary>
/// An inner handler for tests: it records every request and answers from a first-in first-out script per method and path. A
/// request nobody scripted fails the test instead of reaching any network.
/// </summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _script = new(StringComparer.Ordinal);

    /// <summary>An optional log shared with other fakes, so tests can check the order of events.</summary>
    public List<string>? EventLog { get; set; }

    /// <summary>Every request received so far, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Scripts the next answer for a method and path.</summary>
    public RecordingHandler Respond(string method, string path, HttpStatusCode status, string json)
    {
        return Script(method, path, () => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    /// <summary>Scripts the next answer for a method and path as a failure thrown by the transport.</summary>
    public RecordingHandler Throw(string method, string path, Exception exception)
    {
        return Script(method, path, () => throw exception);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method.Method, uri.AbsolutePath, uri.Query, headers, body);

        Func<HttpResponseMessage>? next = null;

        lock (_gate)
        {
            _requests.Add(recorded);
            EventLog?.Add("request " + recorded.Method + " " + recorded.Path);

            if (_script.TryGetValue(Key(recorded.Method, recorded.Path), out var queue) && queue.Count > 0)
            {
                next = queue.Dequeue();
            }
        }

        return next is null
            ? throw new InvalidOperationException($"No scripted answer for {recorded.Method} {recorded.Path}.")
            : next();
    }

    private RecordingHandler Script(string method, string path, Func<HttpResponseMessage> answer)
    {
        lock (_gate)
        {
            var key = Key(method, path);

            if (!_script.TryGetValue(key, out var queue))
            {
                queue = new Queue<Func<HttpResponseMessage>>();
                _script[key] = queue;
            }

            queue.Enqueue(answer);
        }

        return this;
    }

    private static string Key(string method, string path)
    {
        return method + " " + path;
    }
}
