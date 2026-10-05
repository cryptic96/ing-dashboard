namespace Ledger.Domain.Ingestion;

/// <summary>Stores the pending bank authorisations that a callback must present a matching one-time state for.</summary>
public interface IBankAuthorizationStore
{
    /// <summary>Records a pending authorisation under the SHA-256 of its state.</summary>
    Task CreateAsync(PendingAuthorization authorization, CancellationToken cancellationToken);

    /// <summary>
    /// Consumes the authorisation with the given state hash in one atomic update. It succeeds only when the state exists, has not
    /// been used and has not expired, and never succeeds twice for the same state.
    /// </summary>
    Task<ConsumedAuthorization?> TryConsumeAsync(byte[] stateSha256, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The purposes a pending authorisation can have.</summary>
public static class AuthorizationPurposes
{
    /// <summary>A first link of a bank consent.</summary>
    public const string Link = "link";

    /// <summary>A renewal of an existing connection's consent.</summary>
    public const string Renew = "renew";
}

/// <summary>An authorisation waiting for the bank redirect.</summary>
public record PendingAuthorization(
    byte[] StateSha256,
    string Purpose,
    Guid? ConnectionId,
    string ProviderAuthorizationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>What a consumed authorisation was for.</summary>
public record ConsumedAuthorization(string Purpose, Guid? ConnectionId);
