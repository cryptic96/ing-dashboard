using System.Net;

namespace Ledger.Service.Hosting;

/// <summary>Resolves the ops endpoint's port from configuration, refusing to start unless its host is loopback.</summary>
public static class OpsEndpoint
{
    private const string ConfigurationKey = "Kestrel:Endpoints:Ops:Url";

    /// <summary>Reads Kestrel:Endpoints:Ops:Url and returns its port. Throws when the key is missing or its host is not loopback.</summary>
    public static int FromConfiguration(IConfiguration configuration)
    {
        var url = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"Missing required configuration key: {ConfigurationKey}");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must use a loopback host (127.0.0.1, ::1 or localhost).");
        }

        return uri.Port;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = host.Trim('[', ']');

        return IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address);
    }
}
