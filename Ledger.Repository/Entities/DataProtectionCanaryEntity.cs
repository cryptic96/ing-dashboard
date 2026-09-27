namespace Ledger.Repository.Entities;

/// <summary>The single-row table proving the Data Protection key ring can still decrypt what a prior instance protected.</summary>
public class DataProtectionCanaryEntity
{
    /// <summary>Always 1; a check constraint enforces this is the only row.</summary>
    public short Id { get; set; }

    /// <summary>The canary value, protected through <see cref="Ledger.Domain.Security.ISecretProtector"/>.</summary>
    public string ProtectedPayload { get; set; } = string.Empty;

    /// <summary>SHA-256 of the plaintext, used to prove a successful decrypt without ever logging the plaintext.</summary>
    public byte[] PlaintextSha256 { get; set; } = [];

    /// <summary>When the canary row was created. Never updated afterward.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
