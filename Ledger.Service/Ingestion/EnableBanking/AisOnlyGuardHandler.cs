using System.Text.RegularExpressions;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>
/// The only handler between the aggregator client and the network. It refuses every request that is not one of the seven
/// account-information routes on the aggregator's host, so no payment or money-movement route can be reached from this process,
/// whatever the calling code does.
/// </summary>
public sealed partial class AisOnlyGuardHandler : DelegatingHandler
{
    private const string AllowedHost = "api.enablebanking.com";

    /// <summary>The refusal message. It never carries the refused address.</summary>
    public const string RefusalMessage = "Outbound bank request refused: only account-information routes are allowed.";

    [GeneratedRegex(@"\A/sessions/[A-Za-z0-9-]{1,128}\z")]
    private static partial Regex SessionPath();

    [GeneratedRegex(@"\A/accounts/[A-Za-z0-9-]{1,128}/(balances|transactions)\z")]
    private static partial Regex AccountDataPath();

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsAllowed(request))
        {
            throw new InvalidOperationException(RefusalMessage);
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static bool IsAllowed(HttpRequestMessage request)
    {
        var uri = request.RequestUri;

        if (uri is null
            || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort)
        {
            return false;
        }

        var path = uri.AbsolutePath;
        var method = request.Method;

        if (method == HttpMethod.Get)
        {
            return path is "/application" or "/aspsps" || AccountDataPath().IsMatch(path);
        }

        if (method == HttpMethod.Post)
        {
            return path is "/auth" or "/sessions";
        }

        if (method == HttpMethod.Delete)
        {
            return SessionPath().IsMatch(path);
        }

        return false;
    }
}
