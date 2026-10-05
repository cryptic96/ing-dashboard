using Ledger.Domain.Banking;

namespace Ledger.Repository.Entities;

/// <summary>One account-data call made to the bank, recorded before it was sent. Rows are only ever appended.</summary>
public class ProviderCallEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The account the call read.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The sync run that made the call, or null for a call made outside a run.</summary>
    public Guid? SyncRunId { get; set; }

    /// <summary>When the call was made.</summary>
    public DateTimeOffset CalledAt { get; set; }

    /// <summary>What the call read.</summary>
    public ProviderCallKind Kind { get; set; }

    /// <summary>Whether no person was present, which is what the bank budgets.</summary>
    public bool Background { get; set; }
}
