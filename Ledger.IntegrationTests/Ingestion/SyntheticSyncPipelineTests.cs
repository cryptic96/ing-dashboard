using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;

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
}
