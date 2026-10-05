using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion;

/// <summary>Builds the details of the person present during an operator-triggered fetch from the incoming request.</summary>
public static class PsuContextFactory
{
    private const int MaxUserAgentLength = 256;

    /// <summary>
    /// Returns the client address and User-Agent of the request, or null when the option is disabled or either value is missing.
    /// The address is the one already corrected by the forwarded-headers middleware for a known proxy. The User-Agent is cut
    /// to a safe length and stripped of control characters because it is forwarded to the provider as a header.
    /// </summary>
    public static PsuContext? FromRequest(HttpContext context, IngestionOptions options)
    {
        if (!options.PsuHeadersOnOperatorSyncs)
        {
            return null;
        }

        var address = context.Connection.RemoteIpAddress;
        var userAgent = Sanitize(context.Request.Headers.UserAgent.ToString());

        if (address is null || userAgent.Length == 0)
        {
            return null;
        }

        var text = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        return new PsuContext(text, userAgent);
    }

    private static string Sanitize(string value)
    {
        var printable = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return printable.Length > MaxUserAgentLength ? printable[..MaxUserAgentLength] : printable;
    }
}
