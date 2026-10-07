using Ledger.Service.Mcp;

namespace Ledger.Service.OAuth;

/// <summary>
/// Maps what OpenIddict says about a rejected token to one of the fixed reason words used as a metric label. OpenIddict reports
/// every rejection as the same error, so the reason is read from its documentation address, which ends in a stable identifier.
/// </summary>
public static class TokenRejectionReasons
{
    /// <summary>
    /// OpenIddict's identifier for a token that is no longer valid. It reports a token that has passed its lifetime and a token whose
    /// stored entry was revoked in exactly the same words, so <see cref="ConfirmExpiry"/> tells the two apart.
    /// </summary>
    public const string ExpiredIdentifier = "ID2019";

    /// <summary>OpenIddict's identifier for a token that carries no audience.</summary>
    public const string NoAudienceIdentifier = "ID2093";

    /// <summary>OpenIddict's identifier for a token whose audience is not one this server is registered for.</summary>
    public const string UnregisteredAudienceIdentifier = "ID2094";

    /// <summary>OpenIddict's identifier for a refresh token that was already redeemed.</summary>
    public const string RedeemedRefreshTokenIdentifier = "ID2012";

    /// <summary>The error code OpenIddict uses for a rejected grant at the token endpoint.</summary>
    public const string InvalidGrant = "invalid_grant";

    /// <summary>Chooses the reason word for a rejected access token. Anything not recognised, including no information at all, is invalid.</summary>
    /// <param name="error">The error code OpenIddict reported.</param>
    /// <param name="errorDescription">The human-readable description OpenIddict reported.</param>
    /// <param name="errorUri">The documentation address OpenIddict reported.</param>
    public static string Classify(string? error, string? errorDescription, string? errorUri)
    {
        if (EndsWithIdentifier(errorUri, ExpiredIdentifier))
        {
            return McpMetrics.ReasonExpired;
        }

        if (EndsWithIdentifier(errorUri, NoAudienceIdentifier) || EndsWithIdentifier(errorUri, UnregisteredAudienceIdentifier))
        {
            return McpMetrics.ReasonWrongAudience;
        }

        return McpMetrics.ReasonInvalid;
    }

    /// <summary>
    /// Settles a reason that <see cref="Classify"/> gave as expired: it stays expired only when the token's stored entry really has
    /// passed its expiration time, and is invalid when the entry is missing, has no expiry or has not reached it, which is what a
    /// revoked token looks like.
    /// </summary>
    /// <param name="reason">The reason <see cref="Classify"/> returned.</param>
    /// <param name="expiresAt">The expiration time of the token's stored entry, or null when there is none.</param>
    /// <param name="now">The current time.</param>
    public static string ConfirmExpiry(string reason, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (!string.Equals(reason, McpMetrics.ReasonExpired, StringComparison.Ordinal))
        {
            return reason;
        }

        return expiresAt is not null && expiresAt.Value <= now ? McpMetrics.ReasonExpired : McpMetrics.ReasonInvalid;
    }

    /// <summary>Whether a token-endpoint error says an already-redeemed refresh token was presented.</summary>
    /// <param name="error">The error code OpenIddict reported.</param>
    /// <param name="errorDescription">The human-readable description OpenIddict reported.</param>
    /// <param name="errorUri">The documentation address OpenIddict reported.</param>
    public static bool IsRefreshTokenReuse(string? error, string? errorDescription, string? errorUri)
    {
        return string.Equals(error, InvalidGrant, StringComparison.Ordinal)
            && EndsWithIdentifier(errorUri, RedeemedRefreshTokenIdentifier);
    }

    private static bool EndsWithIdentifier(string? errorUri, string identifier)
    {
        return errorUri is not null && errorUri.EndsWith("/" + identifier, StringComparison.Ordinal);
    }
}
