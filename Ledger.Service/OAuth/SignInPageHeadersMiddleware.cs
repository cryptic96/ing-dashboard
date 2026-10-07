using System.Security.Cryptography;

namespace Ledger.Service.OAuth;

/// <summary>
/// Adds the response headers the sign-in and consent pages need: nothing is cached, nothing may frame the page, no script runs
/// and the only style allowed is the page's own, identified by a nonce generated for each response.
/// </summary>
public class SignInPageHeadersMiddleware(RequestDelegate next)
{
    /// <summary>The key under which the style nonce of the current request is kept in <see cref="HttpContext.Items"/>.</summary>
    public const string StyleNonceItemKey = "ledger-style-nonce";

    private const string ConsentFormActions =
        "'self' https://claude.ai https://claude.com http://localhost:* http://127.0.0.1:*";

    /// <summary>Applies the headers to sign-in and consent requests and passes every other request on untouched.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        if (!IsSignInPath(context.Request.Path))
        {
            return next(context);
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[StyleNonceItemKey] = nonce;

        var formAction = context.Request.Path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase)
            ? ConsentFormActions
            : "'self'";

        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] =
            $"default-src 'none'; style-src 'nonce-{nonce}'; form-action {formAction}; frame-ancestors 'none'; base-uri 'none'";

        return next(context);
    }

    private static bool IsSignInPath(PathString path)
    {
        return path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase);
    }
}
