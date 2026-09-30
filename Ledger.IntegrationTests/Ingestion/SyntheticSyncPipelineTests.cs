using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>Proves the provider-agnostic sync pipeline end to end on real PostgreSQL, fed by the synthetic provider.</summary>
[Collection("Database")]
[Trait("Category", "Ingestion")]
public class SyntheticSyncPipelineTests(DatabaseFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 9, 30);

    [Fact]
    public async Task Selected_account_syncs_end_to_end_and_unselected_account_is_never_fetched()
    {
        var scenario = SyntheticBankScenario.Create();
        var selected = scenario.AddAccount(AccountKind.Current);
        var unselected = scenario.AddAccount(AccountKind.Savings);
        scenario.AddTransaction(selected, IngestionTestSupport.Booked("entry-001", -12.34m, Day));
        scenario.AddTransaction(selected, IngestionTestSupport.Booked("entry-002", 1500.00m, Day.AddDays(-1), "Example Employer", "Salary"));
        scenario.AddTransaction(unselected, IngestionTestSupport.Booked("entry-901", 25.00m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: true);

        var result = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        result.Outcome.Should().Be(SyncOutcome.Succeeded);
        result.Inserted.Should().Be(2);

        var selectedKey = connection.Accounts[0].AccountKey;
        var unselectedKey = connection.Accounts[1].AccountKey;

        var reported = await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, selectedKey);
        reported.Should().HaveCount(2);
        reported.Select(row => row.EffectiveDate).Should().Equal("2026-09-30", "2026-09-29");
        (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, unselectedKey)).Should().BeEmpty();
        (await IngestionTestSupport.ReadCountsAsync(fixture, unselectedKey)).Transactions.Should().Be(0);

        scenario.Calls.Where(call => call.AccountUid == unselected.Uid).Should().BeEmpty();
        scenario.Calls.Where(call => call.AccountUid == selected.Uid).Should().NotBeEmpty();

        (await IngestionTestSupport.CountIdentityViolationsAsync(fixture)).Should().Be(0);
    }

    [Fact]
    public async Task Running_the_same_sync_twice_adds_nothing_and_records_no_changes()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-101", -5.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-102", -7.50m, Day.AddDays(-2)));
        scenario.AddTransaction(account, IngestionTestSupport.Booked(null, -3.00m, Day.AddDays(-3), "Example Bakery", "Bread"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        var first = await IngestionTestSupport.SyncAsync(factory, connection.Id);
        var afterFirst = await IngestionTestSupport.ReadCountsAsync(fixture, accountKey);

        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);
        var afterSecond = await IngestionTestSupport.ReadCountsAsync(fixture, accountKey);

        first.Inserted.Should().Be(3);
        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Inserted.Should().Be(0);
        second.Updated.Should().Be(0);
        afterSecond.Should().Be(afterFirst);
        afterFirst.Should().Be(new RowCounts(3, 3, 3));

        var storedRun = await IngestionTestSupport.ReadRunAsync(fixture, second.RunId);
        storedRun.Finished.Should().BeTrue();
        storedRun.Inserted.Should().Be(0);
        storedRun.Updated.Should().Be(0);
    }

    [Fact]
    public async Task Pending_item_arriving_booked_under_the_same_reference_stays_one_row_and_becomes_booked()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-201", -20.00m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        var pending = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);
        pending.Should().ContainSingle().Which.Status.Should().Be("pending");

        scenario.Replace(account, 0, IngestionTestSupport.Booked("entry-201", -20.00m, Day.AddDays(1)));
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        var booked = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);
        booked.Should().ContainSingle();
        booked[0].Id.Should().Be(pending[0].Id);
        booked[0].Status.Should().Be("booked");
        booked[0].BookingDate.Should().Be(Day.AddDays(1));
        second.Updated.Should().Be(1);
        second.Inserted.Should().Be(0);

        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Refs.Should().Be(1);
        (await IngestionTestSupport.CountIdentityViolationsAsync(fixture)).Should().Be(0);
    }

    [Fact]
    public async Task Every_field_is_stored_exactly_as_received_and_reporting_shows_the_effective_date()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, new ProviderTransaction(
            "entry-301",
            ProviderTransactionStatus.Booked,
            -1234.5678m,
            "EUR",
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 9, 29),
            "  Example  Grocer ",
            "XX00SYNT0000000042",
            "Groceries 12/34",
            "{\"note\":\"fields\"}"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        var stored = (await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey)).Should().ContainSingle().Subject;
        stored.Status.Should().Be("booked");
        stored.Amount.Should().Be(-1234.5678m);
        stored.Currency.Should().Be("EUR");
        stored.BookingDate.Should().Be(new DateOnly(2026, 9, 30));
        stored.ValueDate.Should().Be(new DateOnly(2026, 10, 1));
        stored.TransactionDate.Should().Be(new DateOnly(2026, 9, 29));
        stored.CounterpartyName.Should().Be("  Example  Grocer ");
        stored.CounterpartyIban.Should().Be("XX00SYNT0000000042");
        stored.Description.Should().Be("Groceries 12/34");

        var reported = (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey)).Should().ContainSingle().Subject;
        reported.EffectiveDate.Should().Be("2026-09-30");
        reported.BookingDate.Should().Be("2026-09-30");
        reported.ValueDate.Should().Be("2026-10-01");
        reported.Amount.Should().Be(-1234.5678m);
        reported.CounterpartyName.Should().Be("  Example  Grocer ");
    }

    [Fact]
    public async Task Absent_fields_are_stored_as_null_never_as_empty_text_or_defaults()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, new ProviderTransaction(
            "entry-302",
            ProviderTransactionStatus.Booked,
            5.00m,
            "EUR",
            new DateOnly(2026, 9, 30),
            null,
            null,
            null,
            null,
            null,
            "{}"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        var stored = (await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey)).Should().ContainSingle().Subject;
        stored.ValueDate.Should().BeNull();
        stored.TransactionDate.Should().BeNull();
        stored.CounterpartyName.Should().BeNull();
        stored.CounterpartyIban.Should().BeNull();
        stored.Description.Should().BeNull();

        var reported = (await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey)).Should().ContainSingle().Subject;
        reported.ValueDate.Should().BeNull();
        reported.CounterpartyName.Should().BeNull();
        reported.Description.Should().BeNull();
    }

    [Fact]
    public async Task Changed_payload_appends_one_row_and_an_unchanged_payload_appends_none()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-401", -9.99m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Payloads.Should().Be(1);

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Payloads.Should().Be(1);

        scenario.Replace(account, 0, IngestionTestSupport.Booked("entry-401", -9.99m, Day, payloadNote: "changed"));
        var third = await IngestionTestSupport.SyncAsync(factory, connection.Id);
        third.Updated.Should().Be(0);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Payloads.Should().Be(2);

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Should().Be(new RowCounts(1, 1, 2));
    }

    [Fact]
    public async Task Amount_with_too_many_decimals_fails_the_run_as_malformed_and_stores_nothing_for_the_account()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-501", -1.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-502", 1.23456m, Day));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        var result = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        result.Outcome.Should().Be(SyncOutcome.FailedMalformed);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Should().Be(new RowCounts(0, 0, 0));

        var run = await IngestionTestSupport.ReadRunAsync(fixture, result.RunId);
        run.Outcome.Should().Be("failed_malformed");
        run.Finished.Should().BeTrue();
    }

    [Fact]
    public async Task Provider_failure_on_a_later_page_leaves_the_account_untouched_and_the_next_run_stores_every_page_once()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 2;
        var account = scenario.AddAccount(AccountKind.Current);
        for (var index = 1; index <= 5; index++)
        {
            scenario.AddTransaction(account, IngestionTestSupport.Booked($"entry-60{index}", -index, Day.AddDays(-index)));
        }

        scenario.FailOnPage(2, ProviderErrorKind.Transient, "synthetic_timeout");

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        var failed = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        failed.Outcome.Should().Be(SyncOutcome.FailedTransient);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Should().Be(new RowCounts(0, 0, 0));
        var failedRun = await IngestionTestSupport.ReadRunAsync(fixture, failed.RunId);
        failedRun.Outcome.Should().Be("failed_transient");
        failedRun.ProviderError.Should().Be("synthetic_timeout");
        failedRun.Finished.Should().BeTrue();

        var recovered = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        recovered.Outcome.Should().Be(SyncOutcome.Succeeded);
        recovered.Inserted.Should().Be(5);
        (await IngestionTestSupport.ReadCountsAsync(fixture, accountKey)).Should().Be(new RowCounts(5, 5, 5));
        (await IngestionTestSupport.ReadRunAsync(fixture, recovered.RunId)).CallsMade.Should().Be(4);
    }

    [Fact]
    public async Task Starting_a_second_run_while_the_first_is_unfinished_is_refused()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);

        using var scope = factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<ISyncRunStore>();

        var firstRunId = await runs.StartAsync(connection.Id, SyncTrigger.Manual, DateTimeOffset.UtcNow, CancellationToken.None);

        var concurrent = () => runs.StartAsync(connection.Id, SyncTrigger.Scheduled, DateTimeOffset.UtcNow, CancellationToken.None);
        await concurrent.Should().ThrowAsync<SyncAlreadyRunningException>();

        var throughOrchestrator = () => IngestionTestSupport.SyncAsync(factory, connection.Id);
        await throughOrchestrator.Should().ThrowAsync<SyncAlreadyRunningException>();

        await runs.FinishAsync(
            firstRunId,
            new SyncRunCompletion(SyncOutcome.Succeeded, null, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var afterFinish = await IngestionTestSupport.SyncAsync(factory, connection.Id);
        afterFinish.Outcome.Should().Be(SyncOutcome.Succeeded);
    }

    [Fact]
    public async Task Reporting_orders_by_effective_date_then_first_seen_then_id_and_excludes_dropped_rows()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-701", -1m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-702", -2m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-703", -3m, Day.AddDays(-1)));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-704", -4m, Day.AddDays(-1)));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-705", -5m, Day.AddDays(-2)));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        var stored = await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey);
        var expected = stored
            .OrderByDescending(row => row.BookingDate)
            .ThenByDescending(row => row.FirstSeenAt)
            .ThenByDescending(row => Convert.ToHexString(row.Id.ToByteArray(bigEndian: true)), StringComparer.Ordinal)
            .Select(row => row.Id)
            .ToList();

        var reported = await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey);
        reported.Select(row => row.TransactionId).Should().Equal(expected);

        await DropAsync(expected[0]);

        var afterDrop = await IngestionTestSupport.ReadReportingTransactionsAsync(fixture, accountKey);
        afterDrop.Select(row => row.TransactionId).Should().Equal(expected.Skip(1));
    }

    private async Task DropAsync(Guid transactionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_runtime"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE public.transactions SET status = 'dropped', dropped_at = now() WHERE id = @id";
        command.Parameters.AddWithValue("id", transactionId);
        await command.ExecuteNonQueryAsync();
    }
}
