using Ledger.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Reads and creates the single canary row through the ledger database, as the runtime role.</summary>
public class DataProtectionCanaryStore(LedgerDbContext dbContext) : IDataProtectionCanaryStore
{
    /// <inheritdoc />
    public async Task<CanaryRecord?> GetAsync(CancellationToken cancellationToken)
    {
        var entity = await dbContext.DataProtectionCanary
            .AsNoTracking()
            .SingleOrDefaultAsync(canary => canary.Id == 1, cancellationToken);

        return entity is null
            ? null
            : new CanaryRecord(entity.ProtectedPayload, entity.PlaintextSha256, entity.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(CanaryRecord record, CancellationToken cancellationToken)
    {
        var rowsAffected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO data_protection_canary (id, protected_payload, plaintext_sha256, created_at)
             VALUES (1, {record.ProtectedPayload}, {record.PlaintextSha256}, {record.CreatedAt})
             ON CONFLICT (id) DO NOTHING
             """,
            cancellationToken);

        return rowsAffected > 0;
    }
}
