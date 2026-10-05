using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion;

/// <summary>The provider used when no bank link is configured: every operation fails with a not-configured error.</summary>
public class DisabledBankDataProvider : IBankDataProvider
{
    /// <inheritdoc />
    public string Name => "disabled";

    /// <inheritdoc />
    public Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        return Task.FromException<AuthorizationStart>(NotConfigured());
    }

    /// <inheritdoc />
    public Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken)
    {
        return Task.FromException<ProviderSession>(NotConfigured());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
        ProviderAccountRef account,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromException<IReadOnlyList<ProviderBalance>>(NotConfigured());
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
        ProviderAccountRef account,
        TransactionQuery query,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        throw NotConfigured();
    }

    /// <inheritdoc />
    public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        return Task.FromException(NotConfigured());
    }

    private static BankProviderException NotConfigured()
    {
        return new BankProviderException(ProviderErrorKind.NotConfigured, null, "Bank link is not configured.");
    }
}
