using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>
/// An in-memory copy of one account's ledger rows that applies a reconciliation plan with the same semantics as the database
/// store: inserts with their reference, updates (including the upgrade to booked and the restore of a dropped row), merges
/// that add the booked reference to the pending row, unclear-match flags on pending rows and drops of pending rows. It
/// exposes the rows in the shape the reconciler reads, so a replay can plan, apply and plan again.
/// </summary>
public sealed class InMemoryLedger
{
    private readonly List<Row> _rows = [];

    /// <summary>How many rows were inserted.</summary>
    public int Inserted { get; private set; }

    /// <summary>How many pending rows were merged with a booked item under a new reference.</summary>
    public int Merged { get; private set; }

    /// <summary>How many pending rows were flagged as an unclear match.</summary>
    public int Flagged { get; private set; }

    /// <summary>How many pending rows were dropped.</summary>
    public int Dropped { get; private set; }

    /// <summary>How many dropped rows were restored because the bank reported them again.</summary>
    public int Restored { get; private set; }

    /// <summary>How many rows changed field values or status through an update.</summary>
    public int Updated { get; private set; }

    /// <summary>How many pending rows became booked under the same reference.</summary>
    public int Upgraded { get; private set; }

    /// <summary>The number of rows, whatever their status.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The number of rows that are still pending and not dropped.</summary>
    public int LivePending => _rows.Count(row => row.Status == LedgerTransactionStatus.Pending);

    /// <summary>The number of booked rows.</summary>
    public int LiveBooked => _rows.Count(row => row.Status == LedgerTransactionStatus.Booked);

    /// <summary>
    /// The number of references that point at more than one row; always zero for a consistent ledger. The ledger records a
    /// violation instead of refusing it, so a replay can name the broken invariant rather than stop on an exception.
    /// </summary>
    public int ReferencesOnMoreThanOneRow => _rows
        .SelectMany(row => row.Refs.Select(reference => (reference, row.Id)))
        .GroupBy(pair => pair.reference, StringComparer.Ordinal)
        .Count(group => group.Select(pair => pair.Id).Distinct().Count() > 1);

    /// <summary>The rows in the shape the reconciler reads.</summary>
    public IReadOnlyList<LedgerTransactionState> State => _rows
        .Select(row => new LedgerTransactionState(
            row.Id,
            row.Status,
            row.Refs.ToList(),
            row.Amount,
            row.Currency,
            row.BookingDate,
            row.ValueDate,
            row.TransactionDate,
            row.CounterpartyName,
            row.Description,
            row.Flag,
            row.FirstSeenAt))
        .ToList();

    /// <summary>Applies a plan the way the store does, in the same order of parts.</summary>
    /// <exception cref="InvalidOperationException">A plan names a missing row.</exception>
    public void Apply(ReconciliationPlan plan, DateTimeOffset observedAt)
    {
        foreach (var insert in plan.Inserts)
        {
            AddRow(insert, observedAt);
        }

        foreach (var update in plan.Updates)
        {
            ApplyUpdate(update);
        }

        foreach (var merge in plan.Merges)
        {
            ApplyMerge(merge);
        }

        foreach (var id in plan.FlagAmbiguous.Distinct())
        {
            var row = Find(id);
            if (row.Status == LedgerTransactionStatus.Pending && row.Flag != MatchFlag.Ambiguous)
            {
                row.Flag = MatchFlag.Ambiguous;
                Flagged++;
            }
        }

        foreach (var id in plan.Drops.Distinct())
        {
            var row = Find(id);
            if (row.Status == LedgerTransactionStatus.Pending)
            {
                row.Status = LedgerTransactionStatus.Dropped;
                Dropped++;
            }
        }
    }

    private void AddRow(PlannedInsert insert, DateTimeOffset observedAt)
    {
        var item = insert.Item;
        var row = new Row
        {
            Id = Guid.NewGuid(),
            Status = item.Status == ProviderTransactionStatus.Booked ? LedgerTransactionStatus.Booked : LedgerTransactionStatus.Pending,
            Amount = item.Amount,
            Currency = item.Currency,
            BookingDate = item.BookingDate,
            ValueDate = item.ValueDate,
            TransactionDate = item.TransactionDate,
            CounterpartyName = item.CounterpartyName,
            Description = item.Description,
            Flag = insert.Flag,
            FirstSeenAt = observedAt
        };

        Attach(row, insert.Ref);
        _rows.Add(row);
        Inserted++;
    }

    private void ApplyUpdate(PlannedUpdate update)
    {
        var row = Find(update.TransactionId);
        var item = update.Item;
        var changed = false;

        if (update.Restore && row.Status == LedgerTransactionStatus.Dropped)
        {
            row.Status = item.Status == ProviderTransactionStatus.Booked ? LedgerTransactionStatus.Booked : LedgerTransactionStatus.Pending;
            Restored++;
            changed = true;
        }

        changed |= Set(row.BookingDate, item.BookingDate, value => row.BookingDate = value);
        changed |= Set(row.ValueDate, item.ValueDate, value => row.ValueDate = value);
        changed |= Set(row.TransactionDate, item.TransactionDate, value => row.TransactionDate = value);
        changed |= Set(row.Amount, item.Amount, value => row.Amount = value);
        changed |= Set(row.CounterpartyName, item.CounterpartyName, value => row.CounterpartyName = value);
        changed |= Set(row.Description, item.Description, value => row.Description = value);

        if (update.UpgradeToBooked && row.Status == LedgerTransactionStatus.Pending)
        {
            row.Status = LedgerTransactionStatus.Booked;
            Upgraded++;
            changed = true;
        }

        if (row.Status == LedgerTransactionStatus.Booked && row.Flag != MatchFlag.None)
        {
            row.Flag = MatchFlag.None;
            changed = true;
        }

        if (changed)
        {
            Updated++;
        }

    }

    private void ApplyMerge(PlannedMerge merge)
    {
        var row = Find(merge.PendingTransactionId);
        var item = merge.BookedItem;

        row.Status = LedgerTransactionStatus.Booked;
        row.BookingDate = item.BookingDate;
        row.ValueDate = item.ValueDate;
        row.TransactionDate = item.TransactionDate;
        row.CounterpartyName = item.CounterpartyName;
        row.Description = item.Description;
        row.Flag = MatchFlag.None;

        Attach(row, merge.Ref);
        Merged++;
    }

    private void Attach(Row row, string reference)
    {
        row.Refs.Add(reference);
    }

    private Row Find(Guid id)
    {
        return _rows.FirstOrDefault(row => row.Id == id)
            ?? throw new InvalidOperationException("A plan names a row that does not exist.");
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

    private sealed class Row
    {
        public Guid Id { get; init; }

        public LedgerTransactionStatus Status { get; set; }

        public List<string> Refs { get; } = [];

        public decimal Amount { get; set; }

        public string Currency { get; init; } = string.Empty;

        public DateOnly? BookingDate { get; set; }

        public DateOnly? ValueDate { get; set; }

        public DateOnly? TransactionDate { get; set; }

        public string? CounterpartyName { get; set; }

        public string? Description { get; set; }

        public MatchFlag Flag { get; set; }

        public DateTimeOffset FirstSeenAt { get; init; }
    }
}
