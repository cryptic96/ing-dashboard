using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    [Fact]
    public async Task A_revoked_connection_is_never_made_active_again_and_a_superseded_one_keeps_its_closing_time_when_revoked()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var first = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var second = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var supersededAt = await ReadClosedAtAsync(first.Id);
        var laterThanSupersession = (supersededAt ?? DateTimeOffset.UtcNow).AddHours(5);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBankConnectionStore>();
        await store.MarkStatusAsync(second.Id, ConnectionStatus.Revoked, laterThanSupersession, CancellationToken.None);
        await store.MarkStatusAsync(second.Id, ConnectionStatus.Active, laterThanSupersession, CancellationToken.None);
        await store.MarkStatusAsync(second.Id, ConnectionStatus.ProviderExpired, laterThanSupersession, CancellationToken.None);
        await store.MarkStatusAsync(first.Id, ConnectionStatus.Active, laterThanSupersession, CancellationToken.None);
        await store.MarkStatusAsync(first.Id, ConnectionStatus.Revoked, laterThanSupersession, CancellationToken.None);

        var connections = await store.ListConnectionsAsync(CancellationToken.None);
        connections.Single(connection => connection.Id == second.Id).Status.Should().Be(ConnectionStatus.Revoked);
        connections.Single(connection => connection.Id == first.Id).Status.Should().Be(ConnectionStatus.Revoked);
        supersededAt.Should().NotBeNull();
        (await ReadClosedAtAsync(first.Id)).Should().Be(supersededAt);
    }

    [Fact]
    public async Task A_run_that_already_finished_or_was_abandoned_keeps_its_first_outcome()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        using var scope = factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<ISyncRunStore>();
        var started = DateTimeOffset.UtcNow;

        var finishedRun = await runs.StartAsync(connection.Id, SyncTrigger.Manual, started, CancellationToken.None);
        (await runs.FinishAsync(finishedRun, Completion(SyncOutcome.Succeeded, started), CancellationToken.None)).Should().BeTrue();
        (await runs.FinishAsync(finishedRun, Completion(SyncOutcome.FailedTransient, started), CancellationToken.None)).Should().BeFalse();

        var abandonedRun = await runs.StartAsync(connection.Id, SyncTrigger.Manual, started, CancellationToken.None);
        (await runs.AbandonUnfinishedAsync(started, CancellationToken.None)).Should().Be(1);
        (await runs.FinishAsync(abandonedRun, Completion(SyncOutcome.Succeeded, started), CancellationToken.None)).Should().BeFalse();

        var outcomes = (await runs.ListRunsSinceAsync(connection.Id, started.AddMinutes(-1), CancellationToken.None))
            .Select(run => run.Outcome)
            .ToList();
        outcomes.Should().BeEquivalentTo([SyncOutcome.Succeeded, SyncOutcome.Abandoned]);
    }

    private static SyncRunCompletion Completion(SyncOutcome outcome, DateTimeOffset at)
    {
        return new SyncRunCompletion(outcome, null, 0, 0, 0, 0, 0, at);
    }

    private async Task<DateTimeOffset?> ReadClosedAtAsync(Guid connectionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT closed_at FROM public.bank_connections WHERE id = @id";
        command.Parameters.AddWithValue("id", connectionId);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task<IReadOnlyList<ConnectionSummary>> ListAsync(LedgerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBankConnectionStore>().ListConnectionsAsync(CancellationToken.None);
    }
}
