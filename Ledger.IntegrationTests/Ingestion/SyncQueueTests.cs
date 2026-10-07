using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Verifies the sync queue never multiplies a request, never silently drops the sync that must follow a link or renewal, and
/// that a connection approved but never synced still gets its first sync when the queued request was lost.
/// </summary>
[Collection("Database")]
[Trait("Category", "Sync")]
public class SyncQueueTests(DatabaseFixture fixture)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateOnly Monday = new(2026, 10, 26);

    [Fact]
    public async Task A_second_sync_request_for_a_connection_that_is_already_queued_is_refused_with_a_conflict()
    {
        var unreadQueue = new ChannelSyncDispatcher();
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            Scenario(pages: 1),
            Instant(14, 0),
            configureServices: services => services.AddSingleton<ISyncDispatcher>(unreadQueue));
        await host.LinkAsync(selectFirstAccountOnly: true);

        using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");
        using var second = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().Should().Contain("already queued");
        unreadQueue.Reader.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_post_link_sync_that_finds_the_connection_busy_waits_and_then_runs()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture, Scenario(pages: 2), Instant(14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var blocking = await host.StartUnfinishedRunAsync(linked.Id, SyncTrigger.Scheduled);
        var dispatcher = host.Factory.Services.GetRequiredService<ISyncDispatcher>();

        dispatcher.TryEnqueue(new SyncRequest(linked.Id, SyncTrigger.PostLink, FetchContext.Background)).Should().Be(EnqueueResult.Queued);
        await WaitForLogAsync(host, "waits because another run is still going");

        await host.AbandonOrphanedRunsAsync();
        host.Clock.Advance(SyncWorker.BusyRetryDelay + TimeSpan.FromSeconds(1));

        var runs = await host.WaitForFinishedRunsAsync(2);
        runs.Single(run => run.Id == blocking).Outcome.Should().Be("abandoned");
        runs.Where(run => run.Id != blocking).Should().ContainSingle().Which.Outcome.Should().Be("succeeded");
    }

    [Fact]
    public async Task A_renewal_whose_sync_cannot_be_queued_still_renews_and_tells_the_operator_to_start_it()
    {
        var scenario = Scenario(pages: 1);
        var bankProvider = new RenewableSyntheticProvider(scenario);
        var fullQueue = new SwitchableSyncDispatcher();

        await using var host = await BankLinkTestHost.StartWithFactoryAsync(
            fixture,
            new LedgerWebApplicationFactory(
                fixture.ConnectionStringFor("ledger_runtime"),
                configureTestServices: services =>
                {
                    services.AddSingleton<IBankDataProvider>(bankProvider);
                    services.AddSingleton<ISyncDispatcher>(fullQueue);
                },
                additionalConfiguration: new Dictionary<string, string?> { ["BankLink:RedirectUrl"] = BankLinkTestHost.RedirectUrl }));

        var connectionKey = await host.LinkAsync(scenario);
        var accounts = await host.ListAccountsAsync(connectionKey);
        await host.SelectAsync(connectionKey, (accounts[0].GetProperty("accountKey").GetString()!, "Joint", true));
        bankProvider.ExposeRenewedSession = true;
        fullQueue.IsFull = true;

        var renewState = await host.StartRenewAsync(connectionKey);
        using var callback = await host.CallbackAsync(renewState, scenario.AuthorizationCode);

        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("could not be queued");
        host.Factory.CapturedLogMessages.Should().Contain(message => message.Contains("could not be queued because the queue is full", StringComparison.Ordinal));
        (await host.ListConnectionsAsync()).Should().Contain(connection => connection.GetProperty("status").GetString() == "superseded");
    }

    [Fact]
    public async Task A_connection_with_selected_accounts_that_never_synced_gets_its_first_sync_from_the_scheduler_without_the_background_allowance()
    {
        var scenario = Scenario(pages: 6);
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            Instant(5, 0),
            new Dictionary<string, string?>
            {
                ["Ingestion:BackgroundCallsPerDay"] = "4",
                ["Ingestion:PsuHeadersOnOperatorSyncs"] = "false"
            });
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        host.Clock.Advance(TimeSpan.FromMinutes(2));
        (await host.RunDueAsync()).Should().Be(0);

        host.Clock.Advance(TimeSpan.FromMinutes(4));
        (await host.RunDueAsync()).Should().Be(1);

        var run = (await host.ReadRunsAsync()).Should().ContainSingle().Which;
        run.Trigger.Should().Be("post_link");
        run.Outcome.Should().Be("succeeded");
        (await host.ReadTransactionCountAsync(linked.Accounts[0].AccountKey)).Should().Be(6);
    }

    [Fact]
    public async Task A_connection_that_never_synced_for_longer_than_two_hours_is_left_to_the_daily_schedule()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture, Scenario(pages: 1), Instant(5, 0));
        await host.LinkAsync(selectFirstAccountOnly: true);

        host.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        (await host.RunDueAsync()).Should().Be(1);

        (await host.ReadRunsAsync()).Should().ContainSingle().Which.Trigger.Should().Be("scheduled");
    }

    private static async Task WaitForLogAsync(SchedulerTestHost host, string fragment)
    {
        await Wait.UntilAsync(
            () => Task.FromResult(host.Factory.CapturedLogMessages.Any(message => message.Contains(fragment, StringComparison.Ordinal))),
            found => found,
            "the expected log message to appear",
            found => "not logged yet",
            interval: TimeSpan.FromMilliseconds(50));
    }

    private static DateTimeOffset Instant(int hour, int minute)
    {
        return SyncSchedule.InstantFor(Monday, new TimeOnly(hour, minute), Amsterdam);
    }

    private static SyntheticBankScenario Scenario(int pages)
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 1;

        var joint = scenario.AddAccount(AccountKind.Current);
        scenario.AddAccount(AccountKind.Savings);

        for (var index = 0; index < pages; index++)
        {
            scenario.AddTransaction(
                joint,
                IngestionTestSupport.Booked($"entry-{index:000}", -(index + 1) * 1.5m, new DateOnly(2026, 10, 1).AddDays(index)));
        }

        return scenario;
    }
}

/// <summary>A dispatcher that accepts requests until it is told it is full, as a queue that cannot take another request.</summary>
public sealed class SwitchableSyncDispatcher : ISyncDispatcher
{
    /// <summary>Whether the queue reports itself full.</summary>
    public volatile bool IsFull;

    /// <inheritdoc />
    public EnqueueResult TryEnqueue(SyncRequest request)
    {
        return IsFull ? EnqueueResult.Full : EnqueueResult.Queued;
    }
}
