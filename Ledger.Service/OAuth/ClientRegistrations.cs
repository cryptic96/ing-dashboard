namespace Ledger.Service.OAuth;

/// <summary>
/// The only OAuth clients this server knows. Both belong to Anthropic's Claude; no client of any other AI provider is
/// registered, so household data reaches no second AI provider. The client ids are public names, not secrets.
/// </summary>
public static class ClientRegistrations
{
    /// <summary>The client shared by claude.ai, Claude Desktop and the mobile apps, which connect from Anthropic's cloud.</summary>
    public const string HostedClientId = "ledger-claude-hosted";

    /// <summary>The client Claude Code uses, which signs in from the operator's own machine with a loopback redirect.</summary>
    public const string CodeClientId = "ledger-claude-code";

    /// <summary>The one scope that grants read access to the ledger.</summary>
    public const string ReadScope = "ledger.read";

    /// <summary>The display name shown on the consent page for <see cref="HostedClientId"/>.</summary>
    public const string HostedDisplayName = "Claude (claude.ai, Claude Desktop and the mobile apps)";

    /// <summary>The display name shown on the consent page for <see cref="CodeClientId"/>.</summary>
    public const string CodeDisplayName = "Claude Code";

    /// <summary>The exact redirect addresses of the hosted client.</summary>
    public static IReadOnlyList<string> HostedRedirectUris { get; } =
    [
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback"
    ];

    /// <summary>The loopback redirect addresses of the Claude Code client; the port is chosen by the client at run time.</summary>
    public static IReadOnlyList<string> CodeRedirectUris { get; } =
    [
        "http://localhost/callback",
        "http://127.0.0.1/callback"
    ];
}
