using System.Text.Json;
using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion.Synthetic;

/// <summary>
/// A small, fixed, entirely synthetic bank for local development: one current and one savings account with a handful of
/// transactions. It is selected with the Synthetic provider setting, which production startup refuses.
/// </summary>
public static class SyntheticDemoScenario
{
    /// <summary>Builds the demo scenario with transactions dated relative to today so dashboards always show recent data.</summary>
    public static SyntheticBankScenario Create()
    {
        var scenario = SyntheticBankScenario.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var current = scenario.AddAccount(AccountKind.Current);
        var savings = scenario.AddAccount(AccountKind.Savings);

        scenario.AddTransaction(current, Booked("demo-current-001", 2400.00m, today.AddDays(-20), "Example Employer", "Salary"));
        scenario.AddTransaction(current, Booked("demo-current-002", -84.15m, today.AddDays(-12), "Example Grocer", "Groceries"));
        scenario.AddTransaction(current, Booked("demo-current-003", -950.00m, today.AddDays(-10), "Example Landlord", "Rent"));
        scenario.AddTransaction(current, Booked("demo-current-004", -42.30m, today.AddDays(-3), "Example Grocer", "Groceries"));
        scenario.AddTransaction(savings, Booked("demo-savings-001", 300.00m, today.AddDays(-19), "Example Household", "Monthly saving"));

        return scenario;
    }

    /// <summary>Wraps the synthetic provider so a demo consent completes with any non-empty code, since nobody outside the process knows the real one.</summary>
    public static IBankDataProvider CreateProvider(SyntheticBankScenario scenario)
    {
        return new DemoBankDataProvider(new SyntheticBankDataProvider(scenario), scenario);
    }

    private static ProviderTransaction Booked(
        string reference,
        decimal amount,
        DateOnly date,
        string counterparty,
        string description)
    {
        var rawJson = JsonSerializer.Serialize(new { reference, amount, date, counterparty, description });

        return new ProviderTransaction(
            reference,
            ProviderTransactionStatus.Booked,
            amount,
            "EUR",
            date,
            date,
            date,
            counterparty,
            null,
            description,
            rawJson);
    }

    private sealed class DemoBankDataProvider(IBankDataProvider inner, SyntheticBankScenario scenario) : IBankDataProvider
    {
        public string Name => inner.Name;

        public Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
        {
            return inner.StartAuthorizationAsync(request, cancellationToken);
        }

        public Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken)
        {
            return inner.CompleteAuthorizationAsync(scenario.AuthorizationCode, cancellationToken);
        }

        public Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
            ProviderAccountRef account,
            FetchContext context,
            CancellationToken cancellationToken)
        {
            return inner.GetBalancesAsync(account, context, cancellationToken);
        }

        public IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
            ProviderAccountRef account,
            TransactionQuery query,
            FetchContext context,
            CancellationToken cancellationToken)
        {
            return inner.GetTransactionsAsync(account, query, context, cancellationToken);
        }

        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            return inner.RevokeSessionAsync(sessionId, cancellationToken);
        }
    }
}
