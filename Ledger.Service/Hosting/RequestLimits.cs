using System.Threading.RateLimiting;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Ledger.Service.Hosting;

/// <summary>
/// The limits that keep the internet-facing surface cheap to refuse: a fixed-window rate per client address and kind of request,
/// applied before authentication so a flooded token or sign-in request is never processed, and an upper bound on request bodies.
/// </summary>
public static class RequestLimits
{
    /// <summary>The largest request body any endpoint accepts, in bytes (256 KiB).</summary>
    public const int MaxRequestBodyBytes = 262144;

    /// <summary>Sign-in and consent form posts allowed per client address per minute.</summary>
    public const int SignInPostsPerMinute = 10;

    /// <summary>Token endpoint requests allowed per client address per minute.</summary>
    public const int TokenRequestsPerMinute = 30;

    /// <summary>MCP and discovery requests allowed per client address per minute.</summary>
    public const int McpRequestsPerMinute = 300;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Registers the rate limiter and the body size limit.</summary>
    public static IServiceCollection AddLedgerRequestLimits(this IServiceCollection services)
    {
        services.Configure<KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

        services.AddExceptionHandler<RequestRejectionHandler>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(Partition);
        });

        return services;
    }

    /// <summary>
    /// Adds the rate limiter when the sign-in, OAuth and MCP surface is switched on. It belongs after the public host guard and
    /// before authentication.
    /// </summary>
    public static WebApplication UseLedgerRequestLimits(this WebApplication app)
    {
        if (app.Services.GetService<LedgerOAuthSurface>() is not null)
        {
            app.UseRateLimiter();
        }

        return app;
    }

    private static RateLimitPartition<string> Partition(HttpContext context)
    {
        var (kind, permits) = Classify(context.Request);

        if (permits == 0)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{kind}|{client}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private static (string Kind, int Permits) Classify(HttpRequest request)
    {
        var path = request.Path;
        var isPost = HttpMethods.IsPost(request.Method);

        if (isPost
            && (path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase)))
        {
            return ("sign-in", SignInPostsPerMinute);
        }

        if (path.StartsWithSegments("/connect/token", StringComparison.OrdinalIgnoreCase))
        {
            return ("token", TokenRequestsPerMinute);
        }

        if (path.StartsWithSegments(LedgerOAuthOptions.McpPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase))
        {
            return ("mcp", McpRequestsPerMinute);
        }

        return ("other", 0);
    }
}

/// <summary>
/// Answers a request the server itself refused while reading it, such as a body above the size limit, with the status the server
/// chose instead of a 500, and says nothing about why beyond that.
/// </summary>
public sealed class RequestRejectionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not Microsoft.AspNetCore.Http.BadHttpRequestException rejected)
        {
            return false;
        }

        httpContext.Response.StatusCode = rejected.StatusCode;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = rejected.StatusCode, Title = "The request was refused." }
        });

        return true;
    }
}
