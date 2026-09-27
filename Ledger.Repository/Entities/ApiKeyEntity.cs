namespace Ledger.Repository.Entities;

/// <summary>A named per-client API key. Only the key id and the SHA-256 of its secret are stored, never the secret.</summary>
public class ApiKeyEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The operator-chosen client name, unique among active keys.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The public key id embedded in the token, unique across every key.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>SHA-256 of the secret, never the secret itself.</summary>
    public byte[] SecretSha256 { get; set; } = [];

    /// <summary>When the key was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the key was revoked, or null while it is still active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
