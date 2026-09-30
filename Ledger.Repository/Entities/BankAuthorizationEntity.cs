namespace Ledger.Repository.Entities;

/// <summary>A pending bank authorisation. Only the SHA-256 of its one-time state is stored, never the state itself.</summary>
public class BankAuthorizationEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The SHA-256 of the one-time state, unique across rows.</summary>
    public byte[] StateSha256 { get; set; } = [];

    /// <summary>Why the authorisation was started: a first link or a renewal.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The connection being renewed, or null for a first link.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>The provider's identifier for the authorisation attempt.</summary>
    public string ProviderAuthorizationId { get; set; } = string.Empty;

    /// <summary>When the authorisation was started.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the state stops being accepted.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the state was used, or null while it is unused.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
