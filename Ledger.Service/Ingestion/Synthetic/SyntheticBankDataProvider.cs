using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion.Synthetic;

/// <summary>A bank data provider backed by a scripted synthetic scenario, feeding exactly the same pipeline as a real one.</summary>
public class SyntheticBankDataProvider(SyntheticBankScenario scenario) : IBankDataProvider
{
    /// <inheritdoc />
    public string Name => "synthetic";

    /// <inheritdoc />
    public Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        scenario.RecordCall(nameof(StartAuthorizationAsync), null, string.Empty);

        var url = new Uri("https://synthetic.example.org/authorize?state=" + Uri.EscapeDataString(request.State));
        var authorizationId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        return Task.FromResult(new AuthorizationStart(url, authorizationId));
    }

    /// <inheritdoc />
    public Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken)
    {
        scenario.RecordCall(nameof(CompleteAuthorizationAsync), null, string.Empty);

        if (!string.Equals(code, scenario.AuthorizationCode, StringComparison.Ordinal))
        {
            throw new BankProviderException(ProviderErrorKind.ConsentRejected, "invalid_code", "The authorization code was not accepted.");
        }

        var accounts = scenario.Accounts
            .Select(account => new ProviderAccount(
                account.Uid,
                account.IdentificationHash,
                account.Iban,
                account.Name,
                account.Kind.ToString(),
                account.Kind,
                account.Currency))
            .ToList();

        return Task.FromResult(new ProviderSession(scenario.SessionId, scenario.SessionValidUntil, accounts));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
        ProviderAccountRef account,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        scenario.RecordCall(nameof(GetBalancesAsync), account.AccountUid, string.Empty);

        var synthetic = Resolve(account);
        return Task.FromResult(synthetic.Balances);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
        ProviderAccountRef account,
        TransactionQuery query,
        FetchContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var synthetic = Resolve(account);
        var transactions = scenario.TransactionsFor(synthetic, query);
        var pageSize = Math.Max(1, scenario.PageSize);
        var pageCount = Math.Max(1, (transactions.Count + pageSize - 1) / pageSize);

        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scenario.RecordCall(nameof(GetTransactionsAsync), account.AccountUid, Describe(query, pageNumber));

            var failure = scenario.TakeFailureFor(pageNumber);
            if (failure is not null)
            {
                throw failure;
            }

            yield return new ProviderTransactionPage(
                transactions.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList());
        }
    }

    /// <inheritdoc />
    public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        scenario.RecordCall(nameof(RevokeSessionAsync), null, string.Empty);
        return Task.CompletedTask;
    }

    private SyntheticAccount Resolve(ProviderAccountRef account)
    {
        if (!string.Equals(account.SessionId, scenario.SessionId, StringComparison.Ordinal))
        {
            throw new BankProviderException(ProviderErrorKind.ConsentRejected, "unknown_session", "The session is not known.");
        }

        return scenario.FindAccount(account.AccountUid)
            ?? throw new BankProviderException(ProviderErrorKind.ConsentRejected, "unknown_account", "The account is not known.");
    }

    private static string Describe(TransactionQuery query, int pageNumber)
    {
        var from = query.DateFrom?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        return $"depth={query.Depth};from={from};page={pageNumber}";
    }
}
