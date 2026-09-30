using System.Security.Cryptography;
using System.Text;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Repository.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>Reads ledger state for reconciliation and applies a reconciliation plan in one database transaction per account.</summary>
public class LedgerStore(LedgerDbContext dbContext) : ILedgerStore
{
    /// <inheritdoc />
    public async Task<FetchWindow> GetFetchWindowAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var effective = dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId
                && transaction.Status != LedgerTransactionStatus.Dropped)
            .Select(transaction => new
            {
                transaction.Status,
                EffectiveDate = transaction.BookingDate ?? transaction.TransactionDate ?? transaction.ValueDate
            });

        var hasTransactions = await effective.AnyAsync(cancellationToken);
        var latest = await effective.MaxAsync(row => row.EffectiveDate, cancellationToken);
        var oldestPending = await effective
            .Where(row => row.Status == LedgerTransactionStatus.Pending)
            .MinAsync(row => row.EffectiveDate, cancellationToken);

        return new FetchWindow(hasTransactions, latest, oldestPending);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LedgerTransactionState>> LoadStateAsync(
        Guid accountId,
        DateOnly? from,
        CancellationToken cancellationToken)
    {
        var transactions = dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId
                && (from == null
                    || transaction.Status == LedgerTransactionStatus.Pending
                    || (transaction.BookingDate ?? transaction.TransactionDate ?? transaction.ValueDate) >= from));

        var rows = await transactions.ToListAsync(cancellationToken);

        var references = await (
            from reference in dbContext.TransactionRefs.AsNoTracking()
            join transaction in transactions on reference.TransactionId equals transaction.Id
            select new { reference.TransactionId, reference.Ref })
            .ToListAsync(cancellationToken);

        var referencesById = references
            .GroupBy(reference => reference.TransactionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(reference => reference.Ref).ToList());

        return rows
            .Select(row => new LedgerTransactionState(
                row.Id,
                row.Status,
                referencesById.GetValueOrDefault(row.Id) ?? [],
                row.Amount,
                row.Currency,
                row.BookingDate,
                row.ValueDate,
                row.TransactionDate,
                row.CounterpartyName,
                row.Description,
                row.MatchFlag ?? MatchFlag.None,
                row.FirstSeenAt))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(
        Guid accountId,
        Guid syncRunId,
        ReconciliationPlan plan,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var inserted = AddInserts(accountId, syncRunId, plan.Inserts, observedAt);
            var updated = await ApplyUpdatesAsync(syncRunId, plan.Updates, observedAt, cancellationToken);
            var merged = await ApplyMergesAsync(accountId, syncRunId, plan.Merges, observedAt, cancellationToken);
            var flagged = await ApplyFlagsAsync(plan.FlagAmbiguous, observedAt, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ApplyResult(inserted, updated, merged, flagged, 0);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private int AddInserts(Guid accountId, Guid syncRunId, IReadOnlyList<PlannedInsert> inserts, DateTimeOffset observedAt)
    {
        foreach (var insert in inserts)
        {
            var item = insert.Item;
            var status = item.Status == ProviderTransactionStatus.Booked
                ? LedgerTransactionStatus.Booked
                : LedgerTransactionStatus.Pending;

            var transaction = new LedgerTransactionEntity
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                Status = status,
                BookingDate = item.BookingDate,
                ValueDate = item.ValueDate,
                TransactionDate = item.TransactionDate,
                Amount = item.Amount,
                Currency = item.Currency,
                CounterpartyName = item.CounterpartyName,
                CounterpartyIban = item.CounterpartyIban,
                Description = item.Description,
                MatchFlag = insert.Flag == MatchFlag.None ? null : insert.Flag,
                FirstSeenAt = observedAt,
                UpdatedAt = observedAt,
                BookedAt = status == LedgerTransactionStatus.Booked ? observedAt : null
            };

            dbContext.Transactions.Add(transaction);

            dbContext.TransactionRefs.Add(new TransactionRefEntity
            {
                AccountId = accountId,
                Ref = insert.Ref,
                TransactionId = transaction.Id,
                FirstStatus = status,
                FirstSeenAt = observedAt
            });

            dbContext.TransactionPayloads.Add(NewPayload(transaction.Id, syncRunId, item.RawJson, observedAt));
        }

        return inserts.Count;
    }

    private async Task<int> ApplyUpdatesAsync(
        Guid syncRunId,
        IReadOnlyList<PlannedUpdate> updates,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return 0;
        }

        var ids = updates.Select(update => update.TransactionId).Distinct().ToList();

        var entities = await dbContext.Transactions
            .Where(transaction => ids.Contains(transaction.Id))
            .ToDictionaryAsync(transaction => transaction.Id, cancellationToken);

        var latestPayloadHashes = await LoadLatestPayloadHashesAsync(ids, cancellationToken);

        var changedIds = new HashSet<Guid>();

        foreach (var update in updates)
        {
            var entity = entities[update.TransactionId];

            if (ApplyFields(entity, update, observedAt))
            {
                changedIds.Add(entity.Id);
            }

            var hash = Hash(update.Item.RawJson);
            if (!latestPayloadHashes.TryGetValue(entity.Id, out var latest) || !latest.AsSpan().SequenceEqual(hash))
            {
                dbContext.TransactionPayloads.Add(NewPayload(entity.Id, syncRunId, update.Item.RawJson, observedAt));
                latestPayloadHashes[entity.Id] = hash;
            }
        }

        return changedIds.Count;
    }

    private async Task<int> ApplyMergesAsync(
        Guid accountId,
        Guid syncRunId,
        IReadOnlyList<PlannedMerge> merges,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (merges.Count == 0)
        {
            return 0;
        }

        var ids = merges.Select(merge => merge.PendingTransactionId).ToList();
        var entities = await dbContext.Transactions
            .Where(transaction => ids.Contains(transaction.Id))
            .ToDictionaryAsync(transaction => transaction.Id, cancellationToken);

        foreach (var merge in merges)
        {
            var entity = entities[merge.PendingTransactionId];
            var item = merge.BookedItem;

            entity.Status = LedgerTransactionStatus.Booked;
            entity.BookingDate = item.BookingDate;
            entity.ValueDate = item.ValueDate;
            entity.TransactionDate = item.TransactionDate;
            entity.CounterpartyName = item.CounterpartyName;
            entity.CounterpartyIban = item.CounterpartyIban;
            entity.Description = item.Description;
            entity.MatchFlag = null;
            entity.BookedAt = observedAt;
            entity.UpdatedAt = observedAt;

            dbContext.TransactionRefs.Add(new TransactionRefEntity
            {
                AccountId = accountId,
                Ref = merge.Ref,
                TransactionId = entity.Id,
                FirstStatus = LedgerTransactionStatus.Booked,
                FirstSeenAt = observedAt
            });

            dbContext.TransactionPayloads.Add(NewPayload(entity.Id, syncRunId, item.RawJson, observedAt));
        }

        return merges.Count;
    }

    private async Task<int> ApplyFlagsAsync(
        IReadOnlyList<Guid> flagAmbiguous,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (flagAmbiguous.Count == 0)
        {
            return 0;
        }

        var ids = flagAmbiguous.Distinct().ToList();
        var entities = await dbContext.Transactions
            .Where(transaction => ids.Contains(transaction.Id) && transaction.Status == LedgerTransactionStatus.Pending)
            .ToListAsync(cancellationToken);

        var flagged = 0;
        foreach (var entity in entities.Where(entity => entity.MatchFlag != MatchFlag.Ambiguous))
        {
            entity.MatchFlag = MatchFlag.Ambiguous;
            entity.UpdatedAt = observedAt;
            flagged++;
        }

        return flagged;
    }

    private static bool ApplyFields(LedgerTransactionEntity entity, PlannedUpdate update, DateTimeOffset observedAt)
    {
        var item = update.Item;
        var changed = false;

        changed |= Set(entity.BookingDate, item.BookingDate, value => entity.BookingDate = value);
        changed |= Set(entity.ValueDate, item.ValueDate, value => entity.ValueDate = value);
        changed |= Set(entity.TransactionDate, item.TransactionDate, value => entity.TransactionDate = value);
        changed |= Set(entity.Amount, item.Amount, value => entity.Amount = value);
        changed |= Set(entity.CounterpartyName, item.CounterpartyName, value => entity.CounterpartyName = value);
        changed |= Set(entity.CounterpartyIban, item.CounterpartyIban, value => entity.CounterpartyIban = value);
        changed |= Set(entity.Description, item.Description, value => entity.Description = value);

        if (update.UpgradeToBooked && entity.Status == LedgerTransactionStatus.Pending)
        {
            entity.Status = LedgerTransactionStatus.Booked;
            entity.BookedAt = observedAt;
            changed = true;
        }

        if (changed)
        {
            entity.UpdatedAt = observedAt;
        }

        return changed;
    }

    private static bool Set<T>(T current, T incoming, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, incoming))
        {
            return false;
        }

        assign(incoming);
        return true;
    }

    private async Task<Dictionary<Guid, byte[]>> LoadLatestPayloadHashesAsync(
        IReadOnlyList<Guid> transactionIds,
        CancellationToken cancellationToken)
    {
        var observations = await dbContext.TransactionPayloads
            .AsNoTracking()
            .Where(payload => transactionIds.Contains(payload.TransactionId))
            .Select(payload => new { payload.TransactionId, payload.ObservedAt, payload.Id, payload.PayloadSha256 })
            .ToListAsync(cancellationToken);

        return observations
            .GroupBy(observation => observation.TransactionId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(observation => observation.ObservedAt)
                    .ThenByDescending(observation => observation.Id)
                    .First()
                    .PayloadSha256);
    }

    private static TransactionPayloadEntity NewPayload(Guid transactionId, Guid syncRunId, string rawJson, DateTimeOffset observedAt)
    {
        return new TransactionPayloadEntity
        {
            Id = Guid.CreateVersion7(),
            TransactionId = transactionId,
            SyncRunId = syncRunId,
            ObservedAt = observedAt,
            Payload = rawJson,
            PayloadSha256 = Hash(rawJson)
        };
    }

    private static byte[] Hash(string rawJson)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(rawJson));
    }
}
