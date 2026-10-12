using Ledger.Domain.Banking;

namespace Ledger.Repository.Entities;

/// <summary>One balance the bank reported for an account on one local day, with the result of reconciling it. Rows are only ever appended.</summary>
public class BalanceSnapshotEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The account the balance belongs to.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The local calendar day of the snapshot.</summary>
    public DateOnly SnapshotDate { get; set; }

    /// <summary>The provider-neutral balance kind.</summary>
    public BalanceKind Kind { get; set; }

    /// <summary>The balance type exactly as the provider named it.</summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>The balance amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>The three-letter currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The date the bank says the balance is as of, or null when it gave none.</summary>
    public DateOnly? ReferenceDate { get; set; }

    /// <summary>
    /// The booked balance the ledger expected, without pending items, or null when unknown. A balance without a reference date is
    /// expected from the baseline snapshot and the transactions booked since; this figure is a record of the check and is never
    /// read back by a later one.
    /// </summary>
    public decimal? ExpectedAmount { get; set; }

    /// <summary>The bank's balance minus the pending items live at the fetch, minus the expected one, or null when unknown.</summary>
    public decimal? DriftAmount { get; set; }

    /// <summary>True when the balance matched to the cent, false when it did not, null when no verdict was possible.</summary>
    public bool? Reconciled { get; set; }

    /// <summary>
    /// The signed sum of the transactions the ledger held as pending when it read this balance, as their amounts stood then, or
    /// null for snapshots stored before this sum was recorded.
    /// </summary>
    public decimal? PendingAmount { get; set; }

    /// <summary>The sync run that took the snapshot.</summary>
    public Guid SyncRunId { get; set; }

    /// <summary>When the snapshot was stored.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
