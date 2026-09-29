using Ledger.Domain.Auth;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Repository.Stores;

/// <summary>Creates, lists, revokes and validates named API keys, storing only the key id and a SHA-256 hash of the secret.</summary>
public class ApiKeyStore(LedgerDbContext dbContext) : IApiKeyStore
{
    /// <inheritdoc />
    public async Task<CreatedApiKey> CreateAsync(string name, CancellationToken cancellationToken)
    {
        if (!ApiKeyName.IsValid(name))
        {
            throw new ApiKeyOperationException($"'{name}' is not a valid key name.");
        }

        var hasActiveKey = await dbContext.ApiKeys
            .AnyAsync(key => key.Name == name && key.RevokedAt == null, cancellationToken);

        if (hasActiveKey)
        {
            throw new ApiKeyOperationException($"An active key named '{name}' already exists.");
        }

        var (keyId, token, secretSha256) = ApiKeyToken.Generate();

        dbContext.ApiKeys.Add(new ApiKeyEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            KeyId = keyId,
            SecretSha256 = secretSha256,
            CreatedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ApiKeyOperationException($"An active key named '{name}' already exists.");
        }

        return new CreatedApiKey(name, keyId, token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeySummary>> ListAsync(CancellationToken cancellationToken)
    {
        return await dbContext.ApiKeys
            .AsNoTracking()
            .OrderBy(key => key.CreatedAt)
            .Select(key => new ApiKeySummary(key.Name, key.KeyId, key.CreatedAt, key.RevokedAt))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(string name, CancellationToken cancellationToken)
    {
        var activeKey = await dbContext.ApiKeys
            .SingleOrDefaultAsync(key => key.Name == name && key.RevokedAt == null, cancellationToken);

        if (activeKey is null)
        {
            return false;
        }

        activeKey.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<ApiKeyIdentity?> ValidateAsync(string presentedToken, CancellationToken cancellationToken)
    {
        if (!ApiKeyToken.TryParse(presentedToken, out var keyId, out var secret))
        {
            return null;
        }

        var storedKey = await dbContext.ApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(key => key.KeyId == keyId && key.RevokedAt == null, cancellationToken);

        if (storedKey is null || !ApiKeyToken.Matches(secret, storedKey.SecretSha256))
        {
            return null;
        }

        return new ApiKeyIdentity(storedKey.Name, storedKey.KeyId);
    }
}
