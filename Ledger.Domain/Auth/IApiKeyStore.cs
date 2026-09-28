using System.Text.RegularExpressions;

namespace Ledger.Domain.Auth;

/// <summary>Creates, lists, revokes and validates named per-client API keys.</summary>
public interface IApiKeyStore
{
    /// <summary>Creates a new active key with the given name, throwing <see cref="ApiKeyOperationException"/> if the name is invalid or already active.</summary>
    Task<CreatedApiKey> CreateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists every key, active and revoked, ordered by creation time. Never includes a secret.</summary>
    Task<IReadOnlyList<ApiKeySummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Revokes the active key with the given name. Returns false when no active key has that name.</summary>
    Task<bool> RevokeAsync(string name, CancellationToken cancellationToken);

    /// <summary>Validates a presented token, returning the caller's identity for an active token, or null otherwise.</summary>
    Task<ApiKeyIdentity?> ValidateAsync(string presentedToken, CancellationToken cancellationToken);
}

/// <summary>A newly created key: its name, public key id and one-time token.</summary>
public record CreatedApiKey(string Name, string KeyId, string Token);

/// <summary>A key's public metadata. Never carries a secret.</summary>
public record ApiKeySummary(string Name, string KeyId, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>The caller identity attached to a successfully authenticated request.</summary>
public record ApiKeyIdentity(string Name, string KeyId);

/// <summary>The naming rule every API key name must satisfy.</summary>
public static partial class ApiKeyName
{
    [GeneratedRegex("\\A[a-z][a-z0-9-]{1,31}\\z")]
    private static partial Regex Pattern();

    /// <summary>True when the name matches the required pattern.</summary>
    public static bool IsValid(string name) => Pattern().IsMatch(name);
}

/// <summary>Thrown when a key name is invalid, or a create/revoke operation cannot be completed as requested.</summary>
public class ApiKeyOperationException(string message) : Exception(message);
