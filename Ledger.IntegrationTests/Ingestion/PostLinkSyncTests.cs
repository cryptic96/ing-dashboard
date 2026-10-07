using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Verifies the first sync after a link or renewal reads the whole history even when nobody's presence is sent to the bank,
/// because the bank only returns the full history shortly after consent and the allowance for unattended calls is small.
/// </summary>
[Collection("Database")]
[Trait("Category", "Sync")]
public class PostLinkSyncTests(DatabaseFixture fixture)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly BookingDay = new(2026, 10, 1);

    private static readonly IReadOnlyDictionary<string, string?> SmallBudgetWithoutPresence = new Dictionary<string, string?>
    {
        ["Ingestion:BackgroundCallsPerDay"] = "4",
        ["Ingestion:PsuHeadersOnOperatorSyncs"] = "false"
    };

    [Fact]
    public async Task A_post_link_sync_without_the_operators_presence_reads_every_page_beyond_the_background_allowance()
    {
        var scenario = ScenarioWithPages(pages: 8);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(14, 0), SmallBudgetWithoutPresence);
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        var result = await RunAsync(host, linked.Id, SyncTrigger.PostLink);

        result.Outcome.Should().Be(SyncOutcome.Succeeded);
        (await host.ReadTransactionCountAsync(linked.Accounts[0].AccountKey)).Should().Be(8);

        var ledger = await host.ReadCallLedgerAsync(linked.Accounts[0].AccountKey);
        ledger.Count.Should().Be(9);
        ledger.AllBackground.Should().BeTrue();
    }

    [Theory]
    [InlineData(SyncTrigger.Manual)]
    [InlineData(SyncTrigger.Scheduled)]
    public async Task Any_other_sync_without_the_operators_presence_still_stops_at_the_background_allowance(SyncTrigger trigger)
    {
        var scenario = ScenarioWithPages(pages: 8);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(14, 0), SmallBudgetWithoutPresence);
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        var result = await RunAsync(host, linked.Id, trigger);

        result.Outcome.Should().Be(SyncOutcome.QuotaExhausted);
        (await host.ReadTransactionCountAsync(linked.Accounts[0].AccountKey)).Should().Be(0);
    }

    [Fact]
    public async Task A_post_link_sync_that_carries_the_operators_presence_is_recorded_as_attended()
    {
        var scenario = ScenarioWithPages(pages: 2);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(14, 0), SmallBudgetWithoutPresence);
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var attended = new FetchContext(new PsuContext("192.0.2.7", "ledger-tests/1.0"));

        using var scope = host.Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SyncOrchestrator>()
            .SyncConnectionAsync(linked.Id, SyncTrigger.PostLink, attended, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SyncOutcome.Succeeded);
        (await host.ReadCallLedgerAsync(linked.Accounts[0].AccountKey)).NoneBackground.Should().BeTrue();
    }

    private static async Task<SyncRunResult> RunAsync(SchedulerTestHost host, Guid connectionId, SyncTrigger trigger)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<SyncOrchestrator>()
            .SyncConnectionAsync(connectionId, trigger, FetchContext.Background, TestContext.Current.CancellationToken);
    }

    private static DateTimeOffset AmsterdamInstant(int hour, int minute)
    {
        return SyncSchedule.InstantFor(Monday, new TimeOnly(hour, minute), Amsterdam);
    }

    private static SyntheticBankScenario ScenarioWithPages(int pages)
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 1;

        var joint = scenario.AddAccount(AccountKind.Current);
        scenario.AddAccount(AccountKind.Savings);

        for (var index = 0; index < pages; index++)
        {
            scenario.AddTransaction(
                joint,
                IngestionTestSupport.Booked($"entry-{index:000}", -(index + 1) * 1.5m, BookingDay.AddDays(index)));
        }

        return scenario;
    }
}
