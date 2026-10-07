using System.Net;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Does what the reverse proxy in front of the host does: a request for an https address of the public host is sent to the
/// loopback port of the in-process host with the original Host header, X-Forwarded-Proto https and an X-Forwarded-For address.
/// Cookies are kept by this handler per original https address, so Secure cookies behave as they do in a browser. Redirects are
/// never followed, so a test sees every Location header.
/// </summary>
public class ProxyEmulatingHandler : DelegatingHandler
{
    /// <summary>The client address a proxied request carries unless a test chooses another.</summary>
    public const string DefaultClientAddress = "192.0.2.50";

    private readonly int _apiPort;
    private readonly CookieContainer _cookies = new();

    /// <summary>Creates the handler for the host listening on the given loopback port.</summary>
    /// <param name="apiPort">The loopback port of the host's API endpoint.</param>
    /// <param name="clientAddress">The address sent as X-Forwarded-For.</param>
    public ProxyEmulatingHandler(int apiPort, string clientAddress = DefaultClientAddress)
        : base(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        _apiPort = apiPort;
        ClientAddress = clientAddress;
    }

    /// <summary>The address sent as X-Forwarded-For; a test may change it between requests.</summary>
    public string ClientAddress { get; set; }

    /// <summary>The cookies the handler holds, for tests that inspect or clear them.</summary>
    public CookieContainer Cookies => _cookies;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.RequestUri ?? throw new InvalidOperationException("The request has no address.");

        var cookieHeader = _cookies.GetCookieHeader(original);
        if (cookieHeader.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        request.Headers.Host = original.Authority;
        request.Headers.Remove("X-Forwarded-Proto");
        request.Headers.Remove("X-Forwarded-For");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", original.Scheme);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ClientAddress);
        request.RequestUri = new Uri($"http://127.0.0.1:{_apiPort}{original.PathAndQuery}");

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    _cookies.SetCookies(original, setCookie);
                }
            }

            return response;
        }
        finally
        {
            request.RequestUri = original;
        }
    }
}
