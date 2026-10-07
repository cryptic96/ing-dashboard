using Ledger.Service.OAuth;

namespace Ledger.Service.Hosting;

/// <summary>
/// Keeps the public MCP host limited to the MCP and OAuth surface, and keeps that surface off every other host, so a wrong rule in
/// the reverse proxy cannot expose the REST API, the metrics or the dashboards on the public name, nor the OAuth endpoints on the
/// internal names. The sign-in and consent pages are additionally answered only to the configured home and VPN networks. The
/// host is read from the Host header the proxy passes through; no forwarded host header is trusted, and the client address is the
/// one the forwarded-headers middleware derived from the trusted proxy.
/// </summary>
public class PublicHostGuard(RequestDelegate next, LedgerOAuthSurface surface)
{
    private static readonly string[] PublicPaths =
    [
        LedgerOAuthOptions.McpPath,
        LedgerOAuthOptions.ResourceMetadataPath,
        "/.well-known/oauth-authorization-server",
        "/.well-known/openid-configuration",
        "/connect/token",
        "/connect/authorize"
    ];

    private const string AccountPrefix = "/account";
    private const string AuthorizePath = "/connect/authorize";

    private readonly string _publicHostName = surface.Options.PublicHostName;
    private readonly string[] _signInNetworks = surface.Options.SignInNetworks;

    /// <summary>Answers 404 for everything the host must not serve and passes the rest on.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        var path = Normalised(context.Request.Path);
        var isOnPublicHost = string.Equals(context.Request.Host.Host, _publicHostName, StringComparison.OrdinalIgnoreCase);
        var isSurfacePath = IsAccountPath(path) || PublicPaths.Contains(path, StringComparer.OrdinalIgnoreCase);

        if (isOnPublicHost != isSurfacePath)
        {
            return NotFound(context);
        }

        var isSignInPath = IsAccountPath(path) || string.Equals(path, AuthorizePath, StringComparison.OrdinalIgnoreCase);

        if (isOnPublicHost && isSignInPath && !SignInNetworks.Contains(_signInNetworks, context.Connection.RemoteIpAddress))
        {
            return NotFound(context);
        }

        return next(context);
    }

    private static string Normalised(PathString path)
    {
        var value = path.Value ?? string.Empty;

        return value.Length > 1 ? value.TrimEnd('/') : value;
    }

    private static bool IsAccountPath(string path)
    {
        return path.Equals(AccountPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(AccountPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }
}

/// <summary>Adds the <see cref="PublicHostGuard"/> to the request pipeline.</summary>
public static class PublicHostGuardExtensions
{
    /// <summary>
    /// Adds the guard when the sign-in, OAuth and MCP surface is switched on, and does nothing otherwise. It belongs right after
    /// the forwarded-headers middleware, so it runs before rate limiting, routing and authentication.
    /// </summary>
    public static WebApplication UsePublicHostGuard(this WebApplication app)
    {
        if (app.Services.GetService<LedgerOAuthSurface>() is not null)
        {
            app.UseMiddleware<PublicHostGuard>();
        }

        return app;
    }
}
