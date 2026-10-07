using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>Proves how linking, renewing and status changes treat connections and the accounts they own on real PostgreSQL.</summary>
[Collection("Database")]
[Trait("Category", "Consent")]
public class ConnectionLifecycleTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task Linking_accounts_the_ledger_already_knows_supersedes_the_old_connection_that_no_longer_owns_any()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);

        var first = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var second = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);

        var connections = await ListAsync(factory);
        connections.Single(connection => connection.Id == first.Id).Status.Should().Be(ConnectionStatus.Superseded);
        connections.Single(connection => connection.Id == second.Id).Status.Should().Be(ConnectionStatus.Active);
        second.Accounts.Select(account => account.AccountKey).Should().BeEquivalentTo(first.Accounts.Select(account => account.AccountKey));
    }

    [Fact]
    public async Task A_session_that_lists_the_same_account_twice_links_it_once()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBankConnectionStore>();
        var provider = scope.ServiceProvider.GetRequiredService<IBankDataProvider>();
        var session = await provider.CompleteAuthorizationAsync(scenario.AuthorizationCode, CancellationToken.None);
        var doubled = session with { Accounts = [.. session.Accounts, .. session.Accounts] };

        var connection = await store.AddConnectionAsync("synthetic", "Synthetic Bank", "XX", doubled, "protected", DateTimeOffset.UtcNow, CancellationToken.None);

        connection.Accounts.Should().ContainSingle();
    }

    private static async Task<IReadOnlyList<ConnectionSummary>> ListAsync(LedgerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBankConnectionStore>().ListConnectionsAsync(CancellationToken.None);
    }
}
