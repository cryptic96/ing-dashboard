namespace Ledger.Domain.Banking;

/// <summary>
/// Read-only access to a bank data aggregator. It exposes account information only: linking a consent, reading balances and
/// transactions, and ending a session. No payment or money-movement operation exists on this interface, by design.
/// </summary>
public interface IBankDataProvider
{
    /// <summary>A short lowercase identifier for this provider, stored with every connection and account it produces.</summary>
    string Name { get; }

    /// <summary>Starts a consent and returns the URL the operator must open to approve it.</summary>
    Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken);

    /// <summary>Exchanges the one-time code from the redirect for a session and its accounts.</summary>
    Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken);

    /// <summary>Reads the balances of one account of a session.</summary>
    Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
        ProviderAccountRef account,
        FetchContext context,
        CancellationToken cancellationToken);

    /// <summary>Streams every page of transactions for one account, following continuation until the provider signals the end.</summary>
    IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
        ProviderAccountRef account,
        TransactionQuery query,
        FetchContext context,
        CancellationToken cancellationToken);

    /// <summary>Ends a session at the aggregator.</summary>
    Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>The provider-neutral reasons a provider call can fail.</summary>
public enum ProviderErrorKind
{
    /// <summary>A temporary failure such as a timeout or a server error; retrying later may succeed.</summary>
    Transient,

    /// <summary>The provider or the bank refused the call because a call allowance was used up.</summary>
    RateLimited,

    /// <summary>The consent was rejected, revoked or has expired.</summary>
    ConsentRejected,

    /// <summary>The application's own credentials were refused by the provider.</summary>
    ProviderAuth,

    /// <summary>The provider returned data that cannot be stored faithfully.</summary>
    MalformedData,

    /// <summary>No provider is configured.</summary>
    NotConfigured
}

/// <summary>
/// A failure reported by a bank data provider. The message and provider code never carry URLs, tokens, session ids,
/// account numbers or amounts.
/// </summary>
public class BankProviderException(ProviderErrorKind kind, string? providerCode, string message) : Exception(message)
{
    /// <summary>The provider-neutral reason for the failure.</summary>
    public ProviderErrorKind Kind { get; } = kind;

    /// <summary>A short provider-specific code for diagnostics, or null when the provider gave none.</summary>
    public string? ProviderCode { get; } = providerCode;
}
