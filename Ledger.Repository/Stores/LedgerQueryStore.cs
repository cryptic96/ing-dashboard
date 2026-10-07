using System.Data;
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

    /// <inheritdoc />
    public async Task<TotalsData> ReadTotalsAsync(LedgerQueryFilter filter, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        return await InSnapshotAsync(
            async () =>
            {
                var counterpartyNames = filter.CounterpartyNames
                    .Concat(await ResolveNamesAsync(filter.CounterpartyRefs ?? [], cancellationToken))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                var rows = await ReadGroupedRowsAsync(filter, zone, counterpartyNames, cancellationToken);
                var accounts = await ReadAccountsInScopeAsync(filter.AccountKeys, cancellationToken);

                return new TotalsData(
                    rows.Where(row => row.Kind == BookedKind)
                        .Select(row => new TotalsRow(
                            row.Currency,
                            row.PeriodDate,
                            row.AccountKey,
                            row.CounterpartyName,
                            row.MoneyOut,
                            row.MoneyIn,
                            row.Count))
                        .ToList(),
                    Excluded(rows, PendingKind),
                    Excluded(rows, TransferKind),
                    accounts);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveCounterpartyNamesAsync(IReadOnlyList<string> refs, CancellationToken cancellationToken)
    {
        return await InSnapshotAsync(() => ResolveNamesAsync(refs, cancellationToken), cancellationToken);
    }

    private async Task<T> InSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await read();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        var result = await read();
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    private async Task<IReadOnlyList<string>> ResolveNamesAsync(IReadOnlyList<string> refs, CancellationToken cancellationToken)
    {
        if (refs.Count == 0)
        {
            return [];
        }

        var wanted = refs.ToHashSet(StringComparer.Ordinal);

        var names = await dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.CounterpartyName != null
                && transaction.Status != LedgerTransactionStatus.Dropped
                && dbContext.Accounts.Any(account => account.Id == transaction.AccountId && account.SyncEnabled))
            .Select(transaction => transaction.CounterpartyName!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return names
            .Where(name => CounterpartyRef.For(name) is { } reference && wanted.Contains(reference))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<TotalsAccount>> ReadAccountsInScopeAsync(IReadOnlyList<string> accountKeys, CancellationToken cancellationToken)
    {
        var keys = accountKeys.ToList();

        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.SyncEnabled && (keys.Count == 0 || keys.Contains(account.AccountKey)))
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .Select(account => new
            {
                account.AccountKey,
                account.DisplayName,
                account.Currency,
                account.CreatedAt,
                account.BankConnectionId
            })
            .ToListAsync(cancellationToken);

        var lastSuccess = (await dbContext.SyncRuns
                .AsNoTracking()
                .Where(run => run.Outcome == SyncOutcome.Succeeded)
                .GroupBy(run => run.BankConnectionId)
                .Select(group => new { ConnectionId = group.Key, FinishedAt = group.Max(run => run.FinishedAt) })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.ConnectionId, row => row.FinishedAt);

        return accounts
            .Select(account => new TotalsAccount(
                account.AccountKey,
                account.DisplayName,
                account.Currency,
                account.CreatedAt,
                lastSuccess.GetValueOrDefault(account.BankConnectionId)))
            .ToList();
    }

    private async Task<List<GroupedRow>> ReadGroupedRowsAsync(
        LedgerQueryFilter filter,
        TimeZoneInfo zone,
        string[] counterpartyNames,
        CancellationToken cancellationToken)
    {
        var zoneId = zone.Id;
        var from = filter.Range.From;
        var to = filter.Range.To;
        var accountKeys = filter.AccountKeys.ToArray();
        var direction = filter.Direction switch
        {
            MoneyDirection.Out => "out",
            MoneyDirection.In => "in",
            _ => "both"
        };
        var minAmount = filter.MinAmount;
        var maxAmount = filter.MaxAmount;
        var counterpartyActive = filter.HasCounterpartySelection;
        var counterpartyPatterns = filter.CounterpartyTerms.Select(LikePattern.Contains).ToArray();
        var descriptionPatterns = filter.DescriptionTerms.Select(LikePattern.Contains).ToArray();

        return await dbContext.Database.SqlQuery<GroupedRow>($"""
            WITH own_accounts AS (
                SELECT upper(regexp_replace(iban, '[[:space:]]+', '', 'g')) AS iban
                FROM accounts
                WHERE sync_enabled AND iban IS NOT NULL
            ),
            scoped AS (
                SELECT
                    t.status,
                    t.amount,
                    t.currency,
                    t.counterparty_name,
                    a.account_key,
                    CASE WHEN t.status = 'booked'
                        THEN COALESCE(t.booking_date, t.value_date, t.transaction_date, (t.first_seen_at AT TIME ZONE {zoneId}::text)::date)
                        ELSE COALESCE(t.booking_date, (t.first_seen_at AT TIME ZONE {zoneId}::text)::date)
                    END AS period_date,
                    (t.status = 'booked'
                        AND t.counterparty_iban IS NOT NULL
                        AND upper(regexp_replace(t.counterparty_iban, '[[:space:]]+', '', 'g'))
                            IN (SELECT iban FROM own_accounts WHERE iban <> '')) AS is_transfer
                FROM transactions t
                JOIN accounts a ON a.id = t.account_id
                WHERE a.sync_enabled
                  AND t.status IN ('booked', 'pending')
                  AND (cardinality({accountKeys}::text[]) = 0 OR a.account_key = ANY({accountKeys}::text[]))
                  AND ({direction}::text = 'both'
                       OR ({direction}::text = 'out' AND t.amount < 0)
                       OR ({direction}::text = 'in' AND t.amount > 0))
                  AND ({minAmount}::numeric IS NULL OR abs(t.amount) >= {minAmount}::numeric)
                  AND ({maxAmount}::numeric IS NULL OR abs(t.amount) <= {maxAmount}::numeric)
                  AND (NOT {counterpartyActive}::boolean
                       OR btrim(regexp_replace(t.counterparty_name, '[[:space:]]+', ' ', 'g')) ILIKE ANY({counterpartyPatterns}::text[])
                       OR t.counterparty_name = ANY({counterpartyNames}::text[]))
                  AND (cardinality({descriptionPatterns}::text[]) = 0
                       OR btrim(regexp_replace(t.description, '[[:space:]]+', ' ', 'g')) ILIKE ANY({descriptionPatterns}::text[]))
            )
            SELECT
                CASE WHEN status = 'pending' THEN 'pending' WHEN is_transfer THEN 'transfer' ELSE 'booked' END AS "Kind",
                currency AS "Currency",
                period_date AS "PeriodDate",
                account_key AS "AccountKey",
                counterparty_name AS "CounterpartyName",
                COALESCE(SUM(-amount) FILTER (WHERE amount < 0), 0) AS "MoneyOut",
                COALESCE(SUM(amount) FILTER (WHERE amount > 0), 0) AS "MoneyIn",
                COUNT(*)::int AS "Count"
            FROM scoped
            WHERE period_date BETWEEN {from} AND {to}
            GROUP BY 1, currency, period_date, account_key, counterparty_name
            """).ToListAsync(cancellationToken);
    }

    private static List<ExcludedTotals> Excluded(IEnumerable<GroupedRow> rows, string kind)
    {
        return rows
            .Where(row => row.Kind == kind)
            .GroupBy(row => row.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ExcludedTotals(
                group.Key,
                group.Sum(row => row.Count),
                group.Sum(row => row.MoneyOut),
                group.Sum(row => row.MoneyIn)))
            .ToList();
    }

    private const string BookedKind = "booked";
    private const string PendingKind = "pending";
    private const string TransferKind = "transfer";

    private sealed class GroupedRow
    {
        public string Kind { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;

        public DateOnly PeriodDate { get; set; }

        public string AccountKey { get; set; } = string.Empty;

        public string? CounterpartyName { get; set; }

        public decimal MoneyOut { get; set; }

        public decimal MoneyIn { get; set; }

        public int Count { get; set; }
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
