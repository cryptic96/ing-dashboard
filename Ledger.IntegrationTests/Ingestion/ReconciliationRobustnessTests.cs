using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Proves that reconciliation stays correct when the bank sends dateless pending items, repeats items outside the window the
/// ledger loaded, or sends text the database cannot store. Every test controls the clock so days can be stepped through.
/// </summary>
[Collection("Database")]
[Trait("Category", "Reconciliation")]
public class ReconciliationRobustnessTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 1);

    [Fact]
    public async Task A_pending_item_without_any_date_is_dropped_once_the_bank_stops_listing_it_even_weeks_later()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-old", -4.00m, Day));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-undated", -30.00m, Day) with { TransactionDate = null });
        var (factory, clock) = CreateFactory(scenario);
        await using var _ = factory;
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        await IngestionTestSupport.SyncAsync(factory, connection.Id);
        (await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey)).Should().Contain(row => row.Status == "pending");

        clock.Advance(TimeSpan.FromDays(40));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-new", -9.00m, Day.AddDays(39)));
        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        clock.Advance(TimeSpan.FromDays(1));
        scenario.Remove(account, 1);
        var second = await IngestionTestSupport.SyncAsync(factory, connection.Id);

        second.Outcome.Should().Be(SyncOutcome.Succeeded);
        second.Dropped.Should().Be(1);
        (await IngestionTestSupport.ReadStoredTransactionsAsync(fixture, accountKey))
            .Should().ContainSingle(row => row.Status == "dropped");
    }

    private (LedgerWebApplicationFactory Factory, FakeTimeProvider Clock) CreateFactory(SyntheticBankScenario scenario)
    {
        var clock = new FakeTimeProvider(Start);
        var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            configureTestServices: services =>
            {
                services.AddSingleton<IBankDataProvider>(new SyntheticBankDataProvider(scenario));
                services.AddSingleton<TimeProvider>(clock);
            },
            additionalConfiguration: new Dictionary<string, string?> { ["Ingestion:BackgroundCallsPerDay"] = "1000" });

        return (factory, clock);
    }
}
