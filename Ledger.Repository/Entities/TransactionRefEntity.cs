using Ledger.Domain.Ingestion;

namespace Ledger.Repository.Entities;

/// <summary>A provider reference under which a transaction has been seen. Append-only; a transaction can have several.</summary>
public class TransactionRefEntity
{
    /// <summary>The account the reference is unique within.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The provider reference or synthetic fingerprint.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>The one ledger transaction this reference resolves to.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>The status the transaction had when this reference was first seen.</summary>
    public LedgerTransactionStatus FirstStatus { get; set; }

    /// <summary>When the reference was first seen.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }
}
