using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Queries;
using Ledger.Repository.Conventions;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>
/// Reads the ledger tables for the questions Claude asks. It projects only what a result may show, so account numbers and
/// the provider's own account names never leave this class.
/// </summary>
public class LedgerQueryStore(LedgerDbContext dbContext) : ILedgerQueryStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OverviewAccountData>> ReadOverviewAsync(CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.SyncEnabled)
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .Select(account => new
            {
                account.Id,
                account.AccountKey,
                account.DisplayName,
                account.Kind,
                account.Currency,
                account.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var accountIds = accounts.Select(account => account.Id).ToList();

        var bookedRange = (await dbContext.Transactions
                .AsNoTracking()
                .Where(transaction => accountIds.Contains(transaction.AccountId)
                    && transaction.Status == LedgerTransactionStatus.Booked)
                .GroupBy(transaction => transaction.AccountId)
                .Select(group => new
                {
                    AccountId = group.Key,
                    Earliest = group.Min(transaction => transaction.BookingDate ?? transaction.ValueDate ?? transaction.TransactionDate),
                    Latest = group.Max(transaction => transaction.BookingDate ?? transaction.ValueDate ?? transaction.TransactionDate)
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.AccountId);

        var pendingCounts = (await dbContext.Transactions
                .AsNoTracking()
                .Where(transaction => accountIds.Contains(transaction.AccountId)
                    && transaction.Status == LedgerTransactionStatus.Pending)
                .GroupBy(transaction => transaction.AccountId)
                .Select(group => new { AccountId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.AccountId, row => row.Count);

        var result = new List<OverviewAccountData>(accounts.Count);

        foreach (var account in accounts)
        {
            var range = bookedRange.GetValueOrDefault(account.Id);

            result.Add(new OverviewAccountData(
                account.AccountKey,
                account.DisplayName,
                EnumText.ToText(account.Kind),
                account.Currency,
                account.CreatedAt,
                range?.Earliest,
                range?.Latest,
                pendingCounts.GetValueOrDefault(account.Id),
                await ReadLatestBalanceAsync(account.Id, cancellationToken)));
        }

        return result;
    }

    private async Task<OverviewBalanceData?> ReadLatestBalanceAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var latestDate = await dbContext.BalanceSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.AccountId == accountId)
            .OrderByDescending(snapshot => snapshot.SnapshotDate)
            .Select(snapshot => (DateOnly?)snapshot.SnapshotDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestDate is not { } date)
        {
            return null;
        }

        var snapshots = await dbContext.BalanceSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.AccountId == accountId && snapshot.SnapshotDate == date)
            .Select(snapshot => new
            {
                snapshot.Kind,
                snapshot.Amount,
                snapshot.Currency,
                snapshot.ReferenceDate,
                snapshot.SnapshotDate,
                snapshot.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var chosen = snapshots
            .OrderBy(snapshot => BalanceKindRank(snapshot.Kind))
            .ThenByDescending(snapshot => snapshot.CreatedAt)
            .First();

        return new OverviewBalanceData(
            chosen.Amount,
            chosen.Currency,
            EnumText.ToText(chosen.Kind),
            chosen.ReferenceDate ?? chosen.SnapshotDate);
    }

    private static int BalanceKindRank(BalanceKind kind)
    {
        return kind switch
        {
            BalanceKind.ClosingBooked => 0,
            BalanceKind.InterimBooked => 1,
            _ => 2
        };
    }
}
