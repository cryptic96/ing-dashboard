namespace Ledger.Repository.Entities;

/// <summary>A raw provider payload observed for a transaction. Append-only: a row is added only when the payload changed.</summary>
public class TransactionPayloadEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The transaction the payload belongs to.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>The sync run that observed the payload.</summary>
    public Guid SyncRunId { get; set; }

    /// <summary>When the payload was observed.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>The raw provider payload.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the payload text as received, used to tell whether a later observation differs.</summary>
    public byte[] PayloadSha256 { get; set; } = [];
}
