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

    [Fact]
    public async Task Pending_payment_the_bank_stops_listing_is_dropped_and_leaves_reporting_and_the_run_counts_it()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-F1", -30.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-F2", -4.00m, Day, "Example Bakery", "Bread"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey)).Should().HaveCount(2);

        scenario.Remove(account, 0);
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Dropped.Should().Be(1);

        var reported = await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey);
        reported.Should().ContainSingle().Which.Status.Should().Be("booked");

        var dropped = (await ReadLifecycleAsync(accountKey)).Single(row => row.Status == "dropped");
        dropped.DroppedAt.Should().NotBeNull();
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Transactions.Should().Be(2);

        (await ReadRunCountersAsync(second.RunId)).Should().Be(new RunCounters(0, 1));
    }

    [Fact]
    public async Task Failed_fetch_on_the_last_page_never_drops_a_pending_payment()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 1;
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-G1", -30.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-G2", -4.00m, Day, "Example Bakery", "Bread"));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-G3", -6.00m, Day, "Example Cafe", "Coffee"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        scenario.Remove(account, 0);
        scenario.FailOnPage(2, ProviderErrorKind.Transient, "synthetic_timeout");
        var failed = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        failed.Outcome.Should().Be(SyncOutcome.FailedTransient);
        (await ReadLifecycleAsync(accountKey)).Count(row => row.Status == "pending").Should().Be(1);
        (await ReadRunCountersAsync(failed.RunId)).Should().Be(new RunCounters(0, 0));

        var recovered = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        recovered.Outcome.Should().Be(SyncOutcome.Succeeded);
        recovered.Dropped.Should().Be(1);
        (await ReadLifecycleAsync(accountKey)).Count(row => row.Status == "dropped").Should().Be(1);
    }

    [Fact]
    public async Task Empty_feed_never_drops_a_pending_payment()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-H1", -30.00m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        scenario.Remove(account, 0);
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Dropped.Should().Be(0);
        (await ReadLifecycleAsync(accountKey)).Should().ContainSingle().Which.Status.Should().Be("pending");
    }

    [Fact]
    public async Task Cancelled_pending_payment_is_dropped()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        var pending = IngestionTestSupport.Pending("entry-I1", -30.00m, Day);
        scenario.AddTransaction(account, pending);

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        scenario.Replace(account, 0, pending with { Status = ProviderTransactionStatus.Cancelled });
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        second.Dropped.Should().Be(1);
        (await ReadLifecycleAsync(accountKey)).Should().ContainSingle().Which.Status.Should().Be("dropped");
        (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey)).Should().BeEmpty();
    }

    [Fact]
    public async Task Dropped_payment_that_the_bank_lists_again_is_restored_to_the_same_row()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        var pending = IngestionTestSupport.Pending("entry-J1", -30.00m, Day);
        scenario.AddTransaction(account, pending);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-J2", -4.00m, Day, "Example Bakery", "Bread"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        var original = (await ReadLifecycleAsync(accountKey)).Single(row => row.Status == "pending");

        scenario.Remove(account, 0);
        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        (await ReadLifecycleAsync(accountKey)).Count(row => row.Status == "dropped").Should().Be(1);

        scenario.AddTransaction(account, pending);
        var third = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        third.Inserted.Should().Be(0);
        var rows = await ReadLifecycleAsync(accountKey);
        rows.Should().HaveCount(2);
        var restored = rows.Single(row => row.Id == original.Id);
        restored.Status.Should().Be("pending");
        restored.DroppedAt.Should().BeNull();
        (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Flagged_pending_payment_that_books_under_its_own_reference_loses_its_flag()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-K1", -20.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-K2", -20.00m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        scenario.Remove(account, 1);
        scenario.Remove(account, 0);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-K3", -20.00m, Day.AddDays(1)));
        var flaggingRun = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        flaggingRun.Flagged.Should().Be(2);
        flaggingRun.Dropped.Should().Be(0);
        (await ReadRunCountersAsync(flaggingRun.RunId)).Should().Be(new RunCounters(2, 0));

        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-K1", -20.00m, Day.AddDays(1)));
        var bookingRun = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        bookingRun.Updated.Should().Be(1);
        bookingRun.Flagged.Should().Be(0);

        var reported = await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey);
        reported.Should().HaveCount(3);
        reported.Count(row => row.Status == "booked" && row.MatchFlag is null).Should().Be(2);
        reported.Count(row => row.Status == "pending" && row.MatchFlag == "ambiguous").Should().Be(1);
    }

    private async Task<IReadOnlyList<LifecycleRow>> ReadLifecycleAsync(string accountKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.status, t.dropped_at
            FROM public.transactions t
            JOIN public.accounts a ON a.id = t.account_id
            WHERE a.account_key = @accountKey
            ORDER BY t.first_seen_at, t.id
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var rows = new List<LifecycleRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LifecycleRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return rows;
    }

    private async Task<RunCounters> ReadRunCountersAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT flagged, dropped FROM public.sync_runs WHERE id = @runId";
        command.Parameters.AddWithValue("runId", runId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new RunCounters(reader.GetInt32(0), reader.GetInt32(1));
    }

    private sealed record LifecycleRow(Guid Id, string Status, DateTimeOffset? DroppedAt);

    private sealed record RunCounters(int Flagged, int Dropped);

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
