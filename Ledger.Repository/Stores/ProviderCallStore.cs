using Ledger.Domain.Ingestion;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Appends to and reads the ledger of account-data calls. The runtime role cannot change or delete what it wrote.</summary>
public class ProviderCallStore(LedgerDbContext dbContext) : IProviderCallStore
{
    /// <inheritdoc />
    public async Task RecordAsync(ProviderCallRecord call, CancellationToken cancellationToken)
    {
        var entity = new ProviderCallEntity
        {
            Id = Guid.CreateVersion7(),
            AccountId = call.AccountId,
            SyncRunId = call.SyncRunId,
            CalledAt = call.CalledAt,
            Kind = call.Kind,
            Background = call.Background
        };

        dbContext.ProviderCalls.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.Entry(entity).State = EntityState.Detached;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateTimeOffset>> ListBackgroundCallTimesAsync(
        Guid accountId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        return await dbContext.ProviderCalls
            .AsNoTracking()
            .Where(call => call.AccountId == accountId && call.Background && call.CalledAt >= since)
            .OrderBy(call => call.CalledAt)
            .Select(call => call.CalledAt)
            .ToListAsync(cancellationToken);
    }
}
