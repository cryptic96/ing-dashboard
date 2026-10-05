using Ledger.Domain.Banking;

namespace Ledger.Repository.Entities;

/// <summary>A bank account in the ledger, identified internally by its id and across sessions by provider plus identification hash.</summary>
public class LedgerAccountEntity
{
    /// <summary>The row's primary key, immutable and never a provider value.</summary>
    public Guid Id { get; set; }

    /// <summary>A random opaque key used in views and metrics instead of any bank identifier.</summary>
    public string AccountKey { get; set; } = string.Empty;

    /// <summary>The connection currently used to read this account.</summary>
    public Guid BankConnectionId { get; set; }

    /// <summary>The short name of the provider the account was seen through.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's identifier for the account in the current session, which changes when the consent is renewed.</summary>
    public string? ProviderAccountUid { get; set; }

    /// <summary>The provider's stable identifier for the account across sessions.</summary>
    public string IdentificationHash { get; set; } = string.Empty;

    /// <summary>The account number. Stored here only and never exposed through reporting views or metrics.</summary>
    public string? Iban { get; set; }

    /// <summary>The account name as the provider reports it.</summary>
    public string? ProviderName { get; set; }

    /// <summary>The product name as the provider reports it.</summary>
    public string? Product { get; set; }

    /// <summary>The name the household chose for the account.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The kind of account.</summary>
    public AccountKind Kind { get; set; }

    /// <summary>The account's three-letter currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Whether the operator chose to sync this account. Accounts that are not selected are never fetched.</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>When the row was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
