using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Boots the ledger host on real Kestrel sockets, wired to a throwaway database, so port filtering is genuinely exercised.</summary>
public class LedgerWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string ProviderVariable = "Ingestion__Provider";
    private const string CertificatePathVariable = "DataProtection__CertificatePath";
    private const string CertificatePasswordVariable = "DataProtection__CertificatePassword";

    private readonly string _ledgerConnectionString;
    private readonly string? _contentRootOverride;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly IReadOnlyDictionary<string, string?>? _additionalConfiguration;
    private readonly CapturingLoggerProvider _loggerProvider;
    private readonly TestBackendCertificate? _backendCertificate;
    private readonly Dictionary<int, Socket> _reservedSockets = [];
    private IHost? _realHost;

    /// <summary>
    /// Creates the factory. Binds two loopback sockets on port 0 immediately, so the operating system picks the ports and
    /// callers can build clients before starting the host. The sockets stay bound until Kestrel takes them over when it opens
    /// its endpoints, so no other host or process can claim either port in between.
    /// The certificate and the startup environment are process environment values only while the host is built and started,
    /// because the service registration reads them before the factory's configuration overrides apply; the previous values are
    /// put back afterwards, whether or not startup succeeds.
    /// </summary>
    /// <param name="startupEnvironment">Extra environment values for the duration of startup; a null value removes the variable.</param>
    /// <param name="loggerProvider">A capture provider the caller keeps, so the logs of a host that fails to start can still be read.</param>
    /// <param name="backendCertificate">When given, the API endpoint serves https from this certificate and key, the way the deployed host does for the reverse proxy; otherwise it serves plain http.</param>
    public LedgerWebApplicationFactory(
        string ledgerConnectionString,
        string? contentRootOverride = null,
        string? certificatePath = null,
        string? certificatePassword = null,
        Action<IServiceCollection>? configureTestServices = null,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
        IReadOnlyDictionary<string, string?>? startupEnvironment = null,
        CapturingLoggerProvider? loggerProvider = null,
        TestBackendCertificate? backendCertificate = null)
    {
        _loggerProvider = loggerProvider ?? new CapturingLoggerProvider();
        _backendCertificate = backendCertificate;
        _ledgerConnectionString = ledgerConnectionString;
        _contentRootOverride = contentRootOverride;
        _configureTestServices = configureTestServices;
        _additionalConfiguration = additionalConfiguration;
        ApiPort = ReserveLoopbackPort();
        OpsPort = ReserveLoopbackPort();

        var environment = new Dictionary<string, string?>
        {
            [ProviderVariable] = null,
            [CertificatePathVariable] = certificatePath,
            [CertificatePasswordVariable] = certificatePassword
        };

        foreach (var (name, value) in startupEnvironment ?? new Dictionary<string, string?>())
        {
            environment[name] = value;
        }

        try
        {
            using (new EnvironmentOverride(environment))
            {
                EnsureHostStarted();
            }
        }
        catch
        {
            ReleaseReservedSockets();
            throw;
        }
    }

    /// <summary>The real loopback port the API endpoint listens on for this instance.</summary>
    public int ApiPort { get; }

    /// <summary>The real loopback port the ops endpoint listens on for this instance.</summary>
    public int OpsPort { get; }

    /// <summary>
    /// Every log message written by the host so far, across every logging category. The capture sees Debug and Trace messages
    /// too, so a secret logged at a low level cannot hide behind the configured level. The one exception is a category the
    /// committed configuration pins to a level on purpose; it keeps that level here, and the committed configuration tests
    /// verify the pin itself.
    /// </summary>
    public IReadOnlyList<string> CapturedLogMessages => _loggerProvider.Messages;

    /// <summary>An HttpClient bound to the real API port.</summary>
    public HttpClient CreateApiClient() => new() { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}") };

    /// <summary>
    /// An HttpClient bound to the real API port over https that trusts exactly the given certificate, the way the reverse proxy
    /// pins the host certificate; every other certificate is rejected.
    /// </summary>
    public HttpClient CreatePinnedHttpsApiClient(X509Certificate2 pinned)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.Equals(pinned)
            }
        };

        return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{ApiPort}") };
    }

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
                ["Kestrel:Endpoints:Api:Url"] = $"{(_backendCertificate is null ? "http" : "https")}://127.0.0.1:{ApiPort}",
                ["Kestrel:Endpoints:Ops:Url"] = $"http://127.0.0.1:{OpsPort}",
                ["Ingestion:SchedulerEnabled"] = "false"
            });

            if (_backendCertificate is not null)
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kestrel:Endpoints:Api:Certificate:Path"] = _backendCertificate.CertificatePath,
                    ["Kestrel:Endpoints:Api:Certificate:KeyPath"] = _backendCertificate.KeyPath
                });
            }

            if (_additionalConfiguration is not null)
            {
                configurationBuilder.AddInMemoryCollection(_additionalConfiguration);
            }
        });

        builder.ConfigureServices(services =>
        {
            services.Configure<SocketTransportOptions>(options => options.CreateBoundListenSocket = TakeReservedSocket);
        });

        builder.ConfigureLogging((context, logging) =>
        {
            logging.AddProvider(_loggerProvider);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);

            foreach (var pinned in context.Configuration.GetSection("Logging:LogLevel").GetChildren())
            {
                if (pinned.Key != "Default" && Enum.TryParse<LogLevel>(pinned.Value, out var level))
                {
                    logging.AddFilter<CapturingLoggerProvider>(pinned.Key, level);
                }
            }
        });

        if (_configureTestServices is not null)
        {
            builder.ConfigureTestServices(_configureTestServices);
        }
    }

    /// <summary>
    /// The services of the one running host. The base class would hand out the services of its in-memory host, which never
    /// starts; resolving from the running host keeps every test on the same singletons the real endpoints use.
    /// </summary>
    public override IServiceProvider Services => _realHost?.Services ?? base.Services;

    /// <summary>
    /// Builds the Kestrel host that serves every request and runs every background service. The base class insists on an
    /// in-memory test server host and builds the application once per host builder call, so a second, dormant host exists too;
    /// it keeps only the web server itself, so no application background service runs twice and the dormant host opens no
    /// database connection.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var dormant = true;
        builder.ConfigureServices((_, services) =>
        {
            if (dormant)
            {
                RemoveBackgroundServices(services);
            }
        });

        var dormantHost = builder.Build();
        dormant = false;

        builder.ConfigureWebHost(webHostBuilder => webHostBuilder.UseKestrel());

        var realHost = builder.Build();
        realHost.Start();
        _realHost = realHost;

        dormantHost.Start();

        return dormantHost;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseReservedSockets();
            _realHost?.StopAsync().GetAwaiter().GetResult();
            _realHost?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        ReleaseReservedSockets();

        if (_realHost is not null)
        {
            await _realHost.StopAsync();
            _realHost.Dispose();
        }

        await base.DisposeAsync();
    }

    private static void RemoveBackgroundServices(IServiceCollection services)
    {
        var background = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType?.Name != "GenericWebHostService")
            .ToList();

        foreach (var descriptor in background)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>Accesses Server, which is the only thing that makes WebApplicationFactory build and start the host; our own port-bound HttpClients never trigger it on their own.</summary>
    private void EnsureHostStarted()
    {
        _ = Server;
    }

    /// <summary>Binds a loopback socket on port 0, keeps it bound and returns the port the operating system assigned.</summary>
    private int ReserveLoopbackPort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _reservedSockets[port] = socket;
        return port;
    }

    /// <summary>Hands Kestrel the socket reserved for the requested endpoint, or binds a fresh one for any other endpoint.</summary>
    private Socket TakeReservedSocket(EndPoint endpoint)
    {
        lock (_reservedSockets)
        {
            if (endpoint is IPEndPoint ip && _reservedSockets.Remove(ip.Port, out var reserved))
            {
                return reserved;
            }
        }

        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(endpoint);
        return socket;
    }

    private void ReleaseReservedSockets()
    {
        lock (_reservedSockets)
        {
            foreach (var socket in _reservedSockets.Values)
            {
                socket.Dispose();
            }

            _reservedSockets.Clear();
        }
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
