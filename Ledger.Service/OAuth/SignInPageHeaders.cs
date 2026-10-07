using System.Security.Cryptography;

namespace Ledger.Service.OAuth;

/// <summary>
/// Adds the response headers the sign-in and consent pages need: nothing is cached, nothing may frame the page, no script runs,
/// the only style allowed is the page's own, identified by a nonce generated for each response, and a form can only be posted
/// back to the page itself. The consent page widens the form targets to the origins of the requesting client's registered
/// redirect addresses, because browsers apply form-action to the redirect that follows a form post.
/// </summary>
public class SignInPageHeaders(RequestDelegate next)
{
    /// <summary>The key under which the style nonce of the current request is kept in <see cref="HttpContext.Items"/>.</summary>
    public const string StyleNonceItemKey = "ledger-style-nonce";

    private const string SelfOnly = "'self'";

    /// <summary>Applies the headers to sign-in and consent requests and passes every other request on untouched.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        if (!IsSignInPath(context.Request.Path))
        {
            return next(context);
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[StyleNonceItemKey] = nonce;

        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = Policy(nonce, SelfOnly);

        return next(context);
    }

    /// <summary>
    /// Replaces the policy of the current response with one whose form targets are the page itself and the origins of the given
    /// registered redirect addresses. A loopback address allows any port on that host, because a native client picks its port at
    /// run time.
    /// </summary>
    public static void AllowRedirectOrigins(HttpContext context, IEnumerable<string> registeredRedirectUris)
    {
        var nonce = context.Items[StyleNonceItemKey] as string;

        if (nonce is null)
        {
            return;
        }

        var origins = registeredRedirectUris
            .Select(Origin)
            .Where(origin => origin is not null)
            .Distinct(StringComparer.Ordinal);

        context.Response.Headers["Content-Security-Policy"] = Policy(nonce, string.Join(' ', [SelfOnly, .. origins!]));
    }

    private static string Policy(string nonce, string formAction)
    {
        return $"default-src 'none'; style-src 'nonce-{nonce}'; form-action {formAction}; frame-ancestors 'none'; base-uri 'none'";
    }

    private static string? Origin(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.IsLoopback)
        {
            return $"{uri.Scheme}://{uri.Host}:*";
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsSignInPath(PathString path)
    {
        return path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase);
    }
}
