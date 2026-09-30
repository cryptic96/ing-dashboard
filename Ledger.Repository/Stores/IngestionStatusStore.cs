using Ledger.Domain.Ingestion;
using Ledger.Repository.Conventions;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Reads the ingestion state that operational metrics are projected from, with a handful of small queries.</summary>
public class IngestionStatusStore(LedgerDbContext dbContext) : IIngestionStatusStore
{
    private static readonly TimeSpan CallLookback = TimeSpan.FromHours(48);

    /// <inheritdoc />
    public async Task<IngestionStatus> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connections = await dbContext.BankConnections
            .AsNoTracking()
            .Where(connection => connection.Status == BankConnectionEntity.Statuses.Active
                || connection.Status == BankConnectionEntity.Statuses.ProviderExpired)
            .OrderBy(connection => connection.CreatedAt)
            .Select(connection => new { connection.Id, connection.ConnectionKey, connection.Status, connection.ValidUntil })
            .ToListAsync(cancellationToken);

        var connectionIds = connections.Select(connection => connection.Id).ToList();

        var connectionHealth = new List<ConnectionHealth>(connections.Count);

        foreach (var connection in connections)
        {
            var latest = await dbContext.SyncRuns
                .AsNoTracking()
                .Where(run => run.BankConnectionId == connection.Id && run.FinishedAt != null)
                .OrderByDescending(run => run.FinishedAt)
                .Select(run => new SyncRunSummary(run.Trigger, run.StartedAt, run.FinishedAt, run.Outcome))
                .FirstOrDefaultAsync(cancellationToken);

            connectionHealth.Add(new ConnectionHealth(
                connection.ConnectionKey,
                EnumText.Parse<ConnectionStatus>(connection.Status),
                connection.ValidUntil,
                latest));
        }

        var lastSuccessByConnection = (await dbContext.SyncRuns
                .AsNoTracking()
                .Where(run => run.Outcome == SyncOutcome.Succeeded && connectionIds.Contains(run.BankConnectionId))
                .GroupBy(run => run.BankConnectionId)
                .Select(group => new { ConnectionId = group.Key, FinishedAt = group.Max(run => run.FinishedAt) })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.ConnectionId, row => row.FinishedAt);

        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.SyncEnabled && connectionIds.Contains(account.BankConnectionId))
            .OrderBy(account => account.CreatedAt)
            .Select(account => new { account.Id, account.AccountKey, account.BankConnectionId })
            .ToListAsync(cancellationToken);

        var accountIds = accounts.Select(account => account.Id).ToList();
        var keyByConnectionId = connections.ToDictionary(connection => connection.Id, connection => connection.ConnectionKey);

        var callsSince = now - CallLookback;
        var callRows = await dbContext.ProviderCalls
            .AsNoTracking()
            .Where(call => call.Background && call.CalledAt >= callsSince && accountIds.Contains(call.AccountId))
            .Select(call => new { call.AccountId, call.CalledAt })
            .ToListAsync(cancellationToken);
        var callsByAccount = callRows
            .GroupBy(call => call.AccountId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DateTimeOffset>)group.Select(call => call.CalledAt).OrderBy(time => time).ToList());

        var flaggedByAccount = (await dbContext.Transactions
                .AsNoTracking()
                .Where(transaction => accountIds.Contains(transaction.AccountId)
                    && transaction.Status == LedgerTransactionStatus.Pending
                    && transaction.MatchFlag == MatchFlag.Ambiguous)
                .GroupBy(transaction => transaction.AccountId)
                .Select(group => new { AccountId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.AccountId, row => row.Count);

        var accountHealth = new List<AccountHealth>(accounts.Count);

        foreach (var account in accounts)
        {
            var latestReconciled = await dbContext.BalanceSnapshots
                .AsNoTracking()
                .Where(snapshot => snapshot.AccountId == account.Id && snapshot.Reconciled != null)
                .OrderByDescending(snapshot => snapshot.SnapshotDate)
                .ThenByDescending(snapshot => snapshot.CreatedAt)
                .Select(snapshot => snapshot.Reconciled)
                .FirstOrDefaultAsync(cancellationToken);

            accountHealth.Add(new AccountHealth(
                account.AccountKey,
                keyByConnectionId[account.BankConnectionId],
                lastSuccessByConnection.GetValueOrDefault(account.BankConnectionId),
                callsByAccount.GetValueOrDefault(account.Id, []),
                latestReconciled,
                flaggedByAccount.GetValueOrDefault(account.Id)));
        }

        var failedOutcomes = await dbContext.SyncRuns
            .AsNoTracking()
            .Where(run => run.Outcome != null && run.Outcome != SyncOutcome.Succeeded)
            .GroupBy(run => run.Outcome)
            .Select(group => new { Outcome = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);

        var failedByReason = SyncHealth.Reasons.ToDictionary(reason => reason, _ => 0L);

        foreach (var row in failedOutcomes)
        {
            if (row.Outcome is { } outcome && SyncHealth.ReasonForOutcome(outcome) is { } reason)
            {
                failedByReason[reason] += row.Count;
            }
        }

        return new IngestionStatus(connectionHealth, accountHealth, failedByReason);
    }
}
