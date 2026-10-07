namespace Ledger.Service.OAuth;

/// <summary>
/// Compares the resource a client asks for with the one address this server protects. A client may write the address in a
/// different case or with a trailing slash; it may never ask for another address, a query or a fragment.
/// </summary>
public static class ResourceIndicator
{
    /// <summary>
    /// Returns the canonical form of an absolute address: lower-case scheme and host, no default port and no trailing slash.
    /// A query or fragment is dropped.
    /// </summary>
    /// <param name="uri">An absolute address.</param>
    /// <exception cref="ArgumentException">The text is not an absolute address.</exception>
    public static string Canonicalize(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            throw new ArgumentException("The resource is not an absolute address.", nameof(uri));
        }

        var authority = parsed.IsDefaultPort
            ? parsed.Host.ToLowerInvariant()
            : $"{parsed.Host.ToLowerInvariant()}:{parsed.Port}";
        var path = parsed.AbsolutePath;

        if (path.EndsWith('/'))
        {
            path = path[..^1];
        }

        return $"{parsed.Scheme.ToLowerInvariant()}://{authority}{path}";
    }

    /// <summary>
    /// Returns whether the requested resource is the canonical one. An address that is not absolute, or carries a query or a
    /// fragment, never matches.
    /// </summary>
    /// <param name="requested">The resource a client asked for.</param>
    /// <param name="canonical">The address this server protects.</param>
    public static bool IsCanonicalMatch(string requested, string canonical)
    {
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var parsed)
            || parsed.Query.Length > 0
            || parsed.Fragment.Length > 0
            || requested.Contains('?', StringComparison.Ordinal)
            || requested.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(Canonicalize(requested), Canonicalize(canonical), StringComparison.Ordinal);
    }
}
