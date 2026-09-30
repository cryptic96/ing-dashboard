using Ledger.Domain.Ingestion;

namespace Ledger.Repository.Entities;

/// <summary>One transaction in the ledger. Text is stored exactly as the bank sent it; absent fields are NULL.</summary>
public class LedgerTransactionEntity
{
    /// <summary>The row's primary key, immutable and never a provider value.</summary>
    public Guid Id { get; set; }

    /// <summary>The account the transaction belongs to. Immutable.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Whether the transaction is pending, booked or dropped.</summary>
    public LedgerTransactionStatus Status { get; set; }

    /// <summary>The booking date as the bank reported it, as a calendar date.</summary>
    public DateOnly? BookingDate { get; set; }

    /// <summary>The value date as the bank reported it, as a calendar date.</summary>
    public DateOnly? ValueDate { get; set; }

    /// <summary>The transaction date as the bank reported it, as a calendar date.</summary>
    public DateOnly? TransactionDate { get; set; }

    /// <summary>The signed amount, negative for money leaving the account.</summary>
    public decimal Amount { get; set; }

    /// <summary>The three-letter currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The counterparty's name exactly as received, or null.</summary>
    public string? CounterpartyName { get; set; }

    /// <summary>The counterparty's account number exactly as received, or null.</summary>
    public string? CounterpartyIban { get; set; }

    /// <summary>The description exactly as received, or null.</summary>
    public string? Description { get; set; }

    /// <summary>Set to ambiguous when automatic matching could not decide; null otherwise.</summary>
    public MatchFlag? MatchFlag { get; set; }

    /// <summary>When the ledger first saw the transaction. Immutable.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When the ledger last changed the transaction.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the ledger first saw the transaction booked, if it has.</summary>
    public DateTimeOffset? BookedAt { get; set; }

    /// <summary>When the transaction was dropped, if it was.</summary>
    public DateTimeOffset? DroppedAt { get; set; }
}
