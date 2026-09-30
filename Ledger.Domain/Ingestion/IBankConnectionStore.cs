using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>Stores linked bank connections, their accounts and which accounts the operator chose to sync.</summary>
public interface IBankConnectionStore
{
    /// <summary>
    /// Records a newly approved connection and its accounts. The session id is stored only in protected form. An account the
    /// ledger already knows by its stable identification hash keeps its row and its selection.
    /// </summary>
    Task<LinkedConnection> AddConnectionAsync(
        string provider,
        string aspspName,
        string aspspCountry,
        ProviderSession session,
        string protectedSessionId,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken);

    /// <summary>Sets the display name and sync flag of accounts of a connection, identified by their opaque keys.</summary>
    Task SetAccountSelectionAsync(
        Guid connectionId,
        IReadOnlyList<AccountSelection> selections,
        CancellationToken cancellationToken);

    /// <summary>Returns what a sync needs for the connection, or null when the connection does not exist.</summary>
    Task<SyncTarget?> GetSyncTargetAsync(Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>A connection as returned after linking.</summary>
public record LinkedConnection(Guid Id, string ConnectionKey, IReadOnlyList<LinkedAccount> Accounts);

/// <summary>An account of a linked connection, without any secret.</summary>
public record LinkedAccount(
    Guid Id,
    string AccountKey,
    string? Iban,
    string? ProviderName,
    AccountKind Kind,
    string Currency,
    string? DisplayName,
    bool SyncEnabled);

/// <summary>The operator's choice for one account.</summary>
public record AccountSelection(string AccountKey, string? DisplayName, bool SyncEnabled);

/// <summary>Everything a sync run needs to know about one connection.</summary>
public record SyncTarget(
    Guid ConnectionId,
    string ConnectionKey,
    string Provider,
    string ProtectedSessionId,
    IReadOnlyList<SyncAccount> Accounts);

/// <summary>A selected account to sync, with the provider's current identifier for it.</summary>
public record SyncAccount(Guid AccountId, string AccountKey, string ProviderAccountUid);
