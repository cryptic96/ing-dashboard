using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Boots the ledger host on real Kestrel sockets, wired to a throwaway database, so port filtering is genuinely exercised.</summary>
public class LedgerWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _ledgerConnectionString;
    private readonly string? _contentRootOverride;
    private readonly CapturingLoggerProvider _loggerProvider = new();
    private IHost? _realHost;

    /// <summary>Creates the factory. Picks two free loopback ports immediately so callers can build clients before starting the host.</summary>
    public LedgerWebApplicationFactory(string ledgerConnectionString, string? contentRootOverride = null)
    {
        _ledgerConnectionString = ledgerConnectionString;
        _contentRootOverride = contentRootOverride;
        ApiPort = GetFreeLoopbackPort();
        OpsPort = GetFreeLoopbackPort();

        EnsureHostStarted();
    }

    /// <summary>The real loopback port the API endpoint listens on for this instance.</summary>
    public int ApiPort { get; }

    /// <summary>The real loopback port the ops endpoint listens on for this instance.</summary>
    public int OpsPort { get; }

    /// <summary>Every log message written by the host so far, across every logging category.</summary>
    public IReadOnlyList<string> CapturedLogMessages => _loggerProvider.Messages;

    /// <summary>An HttpClient bound to the real API port.</summary>
    public HttpClient CreateApiClient() => new() { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}") };

    /// <summary>An HttpClient bound to the real ops port.</summary>
    public HttpClient CreateOpsClient() => new() { BaseAddress = new Uri($"http://127.0.0.1:{OpsPort}") };

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        if (_contentRootOverride is not null)
        {
            builder.UseContentRoot(_contentRootOverride);
        }

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ledger"] = _ledgerConnectionString,
                ["Kestrel:Endpoints:Api:Url"] = $"http://127.0.0.1:{ApiPort}",
                ["Kestrel:Endpoints:Ops:Url"] = $"http://127.0.0.1:{OpsPort}"
            });
        });

        builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var testHost = builder.Build();

        builder.ConfigureWebHost(webHostBuilder => webHostBuilder.UseKestrel());

        var realHost = builder.Build();
        realHost.Start();
        _realHost = realHost;

        var addresses = realHost.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();

        testHost.Start();
        var testHostAddresses = testHost.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        testHostAddresses.Clear();
        foreach (var address in addresses!.Addresses)
        {
            testHostAddresses.Add(address);
        }

        return testHost;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _realHost?.StopAsync().GetAwaiter().GetResult();
            _realHost?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_realHost is not null)
        {
            await _realHost.StopAsync();
            _realHost.Dispose();
        }

        await base.DisposeAsync();
    }

    /// <summary>Accesses Server, which is the only thing that makes WebApplicationFactory build and start the host; our own port-bound HttpClients never trigger it on their own.</summary>
    private void EnsureHostStarted()
    {
        _ = Server;
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>Captures every formatted log message written by the host, so tests can assert nothing sensitive was logged.</summary>
public class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>Every captured message so far, each prefixed with its logging category.</summary>
    public IReadOnlyList<string> Messages => _messages.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue($"{categoryName}: {formatter(state, exception)}");

            if (exception is not null)
            {
                messages.Enqueue(exception.ToString());
            }
        }
    }
}
