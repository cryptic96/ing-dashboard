using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Appends the daily balance snapshots and reads what reconciliation compares them with. The runtime role cannot change or delete a snapshot.</summary>
public class BalanceStore(LedgerDbContext dbContext) : IBalanceStore
{
    /// <inheritdoc />
    public async Task<bool> HasSnapshotForDateAsync(Guid accountId, DateOnly localDate, CancellationToken cancellationToken)
    {
        return await dbContext.BalanceSnapshots
            .AsNoTracking()
            .AnyAsync(snapshot => snapshot.AccountId == accountId && snapshot.SnapshotDate == localDate, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BalanceSnapshotState?> GetLatestBeforeAsync(
        Guid accountId,
        BalanceKind kind,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        return await dbContext.BalanceSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.AccountId == accountId && snapshot.Kind == kind && snapshot.SnapshotDate < localDate)
            .OrderByDescending(snapshot => snapshot.SnapshotDate)
            .ThenByDescending(snapshot => snapshot.CreatedAt)
            .Select(snapshot => new BalanceSnapshotState(
                snapshot.Kind,
                snapshot.Amount,
                snapshot.Currency,
                snapshot.ReferenceDate,
                snapshot.SnapshotDate,
                snapshot.CreatedAt,
                snapshot.ExpectedAmount))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<decimal> SumBookedAsync(
        Guid accountId,
        DateOnly afterExclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken)
    {
        var sum = await dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId
                && transaction.Status == LedgerTransactionStatus.Booked
                && transaction.BookingDate > afterExclusive
                && transaction.BookingDate <= toInclusive)
            .SumAsync(transaction => (decimal?)transaction.Amount, cancellationToken);

        return sum ?? 0m;
    }

    /// <inheritdoc />
    public async Task<decimal> SumBookedSinceAsync(
        Guid accountId,
        DateTimeOffset afterExclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken)
    {
        var sum = await dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId
                && transaction.Status == LedgerTransactionStatus.Booked
                && transaction.BookedAt > afterExclusive
                && transaction.BookedAt <= toInclusive)
            .SumAsync(transaction => (decimal?)transaction.Amount, cancellationToken);

        return sum ?? 0m;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        Guid accountId,
        Guid syncRunId,
        DateOnly localDate,
        IReadOnlyList<ProviderBalance> balances,
        BalanceKind? reconciledKind,
        BalanceCheck? check,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var entities = balances
            .GroupBy(balance => balance.Kind)
            .Select(group => group.First())
            .Select(balance => ToEntity(balance, accountId, syncRunId, localDate, reconciledKind, check, createdAt))
            .ToList();

        if (entities.Count == 0)
        {
            return;
        }

        dbContext.BalanceSnapshots.AddRange(entities);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            foreach (var entity in entities)
            {
                dbContext.Entry(entity).State = EntityState.Detached;
            }
        }
    }

    private static BalanceSnapshotEntity ToEntity(
        ProviderBalance balance,
        Guid accountId,
        Guid syncRunId,
        DateOnly localDate,
        BalanceKind? reconciledKind,
        BalanceCheck? check,
        DateTimeOffset createdAt)
    {
        var verdict = reconciledKind == balance.Kind ? check : null;

        return new BalanceSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            AccountId = accountId,
            SnapshotDate = localDate,
            Kind = balance.Kind,
            ProviderType = balance.ProviderType,
            Amount = balance.Amount,
            Currency = balance.Currency,
            ReferenceDate = balance.ReferenceDate,
            ExpectedAmount = verdict?.Expected,
            DriftAmount = verdict?.Drift,
            Reconciled = verdict?.Reconciled,
            SyncRunId = syncRunId,
            CreatedAt = createdAt
        };
    }
}
