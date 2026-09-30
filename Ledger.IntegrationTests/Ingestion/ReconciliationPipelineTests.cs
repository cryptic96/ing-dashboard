using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>Proves pending-to-booked reconciliation across several real sync runs on PostgreSQL, fed by the synthetic provider.</summary>
[Collection("Database")]
[Trait("Category", "Reconciliation")]
public class ReconciliationPipelineTests(DatabaseFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 9, 28);

    [Fact]
    public async Task Pending_payment_that_books_under_a_new_reference_stays_one_transaction_with_both_references()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-A", -12.50m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        var pending = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);
        pending.Should().ContainSingle().Which.Status.Should().Be("pending");

        scenario.Replace(account, 0, IngestionTestSupport.Booked("entry-B", -12.50m, Day.AddDays(1)));
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Inserted.Should().Be(0);

        var stored = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);
        stored.Should().ContainSingle();
        stored[0].Id.Should().Be(pending[0].Id);
        stored[0].Status.Should().Be("booked");
        stored[0].BookingDate.Should().Be(Day.AddDays(1));

        (await ReadReferencesAsync(accountKey)).Should().BeEquivalentTo(
            new[] { ("er:entry-A", stored[0].Id), ("er:entry-B", stored[0].Id) });

        var counts = await IngestionTestSupport.ReadCountsAsync(fixture, accountKey);
        counts.Transactions.Should().Be(1);
        counts.Refs.Should().Be(2);
        counts.Payloads.Should().Be(2);
        (await IngestionTestSupport.CountIdentityViolationsAsync(fixture)).Should().Be(0);
    }

    [Fact]
    public async Task Pending_reference_reappearing_after_the_payment_booked_creates_no_duplicate()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-C", -8.00m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        scenario.Replace(account, 0, IngestionTestSupport.Booked("entry-D", -8.00m, Day.AddDays(1)));
        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        var booked = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);

        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-C", -8.00m, Day));
        var third = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        third.Outcome.Should().Be(SyncOutcome.Succeeded);
        third.Inserted.Should().Be(0);

        var stored = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);
        stored.Should().ContainSingle();
        stored[0].Id.Should().Be(booked[0].Id);
        stored[0].Status.Should().Be("booked");
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Transactions.Should().Be(1);
    }

    [Fact]
    public async Task Two_pending_candidates_for_one_booked_item_are_flagged_and_the_booked_item_is_stored_separately()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-E1", -20.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-E2", -20.00m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        scenario.Remove(account, 1);
        scenario.Remove(account, 0);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-E3", -20.00m, Day.AddDays(1)));
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Inserted.Should().Be(1);
        second.Flagged.Should().Be(2);

        var reported = await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey);
        reported.Should().HaveCount(3);
        reported.Count(row => row.Status == "pending" && row.MatchFlag == "ambiguous").Should().Be(2);
        reported.Count(row => row.Status == "booked" && row.MatchFlag is null).Should().Be(1);
    }

    private async Task<IReadOnlyList<(string Ref, Guid TransactionId)>> ReadReferencesAsync(string accountKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.ref, r.transaction_id
            FROM public.transaction_refs r
            JOIN public.accounts a ON a.id = r.account_id
            WHERE a.account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var rows = new List<(string, Guid)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetGuid(1)));
        }

        return rows;
    }
}
