namespace Ledger.Service.OAuth;

/// <summary>
/// Registered only when the sign-in, OAuth and MCP surface was switched on at registration time. Mapping the endpoints checks for
/// it, so what is mapped always matches what was registered, whatever configuration is read later.
/// </summary>
/// <param name="Options">The options the surface was registered with.</param>
public sealed record LedgerOAuthSurface(LedgerOAuthOptions Options);
