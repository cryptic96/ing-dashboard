namespace Ledger.Domain.Security;

/// <summary>A protected value paired with the hash of its plaintext, used to prove the key ring can still decrypt.</summary>
public record CanaryRecord(string ProtectedPayload, byte[] PlaintextSha256, DateTimeOffset CreatedAt);

/// <summary>Reads and creates the single canary row that proves the Data Protection key ring is intact.</summary>
public interface IDataProtectionCanaryStore
{
    /// <summary>Returns the canary row, or null when it has not been created yet.</summary>
    Task<CanaryRecord?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Attempts to create the canary row. Returns false when a row already exists; never overwrites an existing row.</summary>
    Task<bool> TryCreateAsync(CanaryRecord record, CancellationToken cancellationToken);
}
