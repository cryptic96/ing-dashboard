namespace Ledger.Service.OAuth;

/// <summary>
/// Settings of the sign-in, OAuth and MCP surface, bound from the "OAuth" configuration section. Every URL the surface
/// publishes is derived from <see cref="PublicBaseUrl"/> and never from the incoming request, so a proxy in front cannot
/// change what the discovery documents say.
/// </summary>
public class LedgerOAuthOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "OAuth";

    /// <summary>The path the MCP endpoint is served on.</summary>
    public const string McpPath = "/mcp";

    /// <summary>The path the protected-resource metadata document is served on.</summary>
    public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource/mcp";

    /// <summary>
    /// The public base address of the host that serves the sign-in, OAuth and MCP surface, for example https://mcp.example.com.
    /// When it is absent none of that surface is mapped.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How long an access token is valid.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a refresh token is valid without use; every refresh issues a new one with a fresh lifetime.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long after a refresh the old refresh token still works, so a refresh whose answer was lost can be retried.</summary>
    public TimeSpan RefreshTokenReuseLeeway { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a public base address is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(PublicBaseUrl);

    /// <summary>The issuer of the authorization server.</summary>
    public Uri Issuer => new(BaseUri().GetLeftPart(UriPartial.Authority), UriKind.Absolute);

    /// <summary>The issuer exactly as the authorization-server metadata publishes it.</summary>
    public string IssuerText => Issuer.AbsoluteUri;

    /// <summary>The canonical address of the MCP endpoint, which is the audience of every access token.</summary>
    public string ResourceUrl => ResourceIndicator.Canonicalize(BaseText() + McpPath);

    /// <summary>The absolute address of the protected-resource metadata document.</summary>
    public string ResourceMetadataUrl => BaseText() + ResourceMetadataPath;

    /// <summary>The host, with its port when it is not the default one, that serves the surface.</summary>
    public string PublicHost => BaseUri().Authority;

    private string BaseText()
    {
        return BaseUri().GetLeftPart(UriPartial.Authority);
    }

    private Uri BaseUri()
    {
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{SectionName}:PublicBaseUrl is not an absolute address.");
        }

        return uri;
    }
}
