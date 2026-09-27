using System.Reflection;

namespace Ledger.Service.Endpoints;

/// <summary>Maps the authenticated status endpoint used to prove a caller's key works.</summary>
public static class StatusEndpoints
{
    /// <summary>Maps GET /api/v1/status, returning the running version and the caller's key name. Requires authentication like every other endpoint.</summary>
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/status", (HttpContext context) =>
        {
            var version = InformationalVersionWithoutSuffix(Assembly.GetExecutingAssembly());
            var client = context.User.Identity?.Name ?? string.Empty;

            return Results.Ok(new StatusResponse(version, client));
        });

        return endpoints;
    }

    private static string InformationalVersionWithoutSuffix(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }

    private sealed record StatusResponse(string Version, string Client);
}
