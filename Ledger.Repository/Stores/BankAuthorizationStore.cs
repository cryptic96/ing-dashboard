using Ledger.Domain.Ingestion;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Stores pending authorisations by the SHA-256 of their state and consumes each one at most once.</summary>
public class BankAuthorizationStore(LedgerDbContext dbContext) : IBankAuthorizationStore
{
    /// <inheritdoc />
    public async Task CreateAsync(PendingAuthorization authorization, CancellationToken cancellationToken)
    {
        dbContext.BankAuthorizations.Add(new BankAuthorizationEntity
        {
            Id = Guid.CreateVersion7(),
            StateSha256 = authorization.StateSha256,
            Purpose = authorization.Purpose,
            ConnectionId = authorization.ConnectionId,
            ProviderAuthorizationId = authorization.ProviderAuthorizationId,
            CreatedAt = authorization.CreatedAt,
            ExpiresAt = authorization.ExpiresAt
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConsumedAuthorization?> TryConsumeAsync(
        byte[] stateSha256,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var changed = await dbContext.BankAuthorizations
            .Where(authorization => authorization.StateSha256 == stateSha256
                && authorization.ConsumedAt == null
                && authorization.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(authorization => authorization.ConsumedAt, (DateTimeOffset?)now),
                cancellationToken);

        if (changed != 1)
        {
            return null;
        }

        return await dbContext.BankAuthorizations
            .AsNoTracking()
            .Where(authorization => authorization.StateSha256 == stateSha256)
            .Select(authorization => new ConsumedAuthorization(authorization.Purpose, authorization.ConnectionId))
            .SingleAsync(cancellationToken);
    }
}
