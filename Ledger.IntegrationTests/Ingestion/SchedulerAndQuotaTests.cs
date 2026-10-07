using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Security;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository;
using Ledger.Repository.Stores;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Drives the daily scheduler, its retry and quota rules and the sync-now call on real PostgreSQL. Every test gets its own
/// database because a scheduler pass visits every active connection, and its own controllable clock.
/// </summary>
[Collection("Database")]
[Trait("Category", "Sync")]
public class SchedulerAndQuotaTests(DatabaseFixture fixture)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly BookingDay = new(2026, 10, 20);

    private static readonly IReadOnlyDictionary<string, string?> SmallCallBudget = new Dictionary<string, string?>
    {
        ["Ingestion:BackgroundCallsPerDay"] = "4"
    };

    [Fact]
    public async Task The_scheduler_starts_exactly_one_scheduled_run_at_half_past_six_Amsterdam_time()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 29));
        await host.LinkAsync(selectFirstAccountOnly: false);

        (await host.RunDueAsync()).Should().Be(0);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await host.RunDueAsync()).Should().Be(1);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await host.RunDueAsync()).Should().Be(0);

        var runs = await host.ReadRunsAsync();
        runs.Should().ContainSingle();
        runs[0].Trigger.Should().Be("scheduled");
        runs[0].Outcome.Should().Be("succeeded");
    }

    [Fact]
    public async Task Every_provider_call_of_a_scheduled_run_is_in_the_call_ledger_for_selected_accounts_only()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 3);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        var selected = linked.Accounts[0];
        var unselected = linked.Accounts[1];
        var pageCalls = scenario.Calls.Count(call => call.Method == nameof(IBankDataProvider.GetTransactionsAsync));

        pageCalls.Should().Be(3);

        var ledger = await host.ReadCallLedgerAsync(selected.AccountKey);
        ledger.Count.Should().Be(pageCalls + 1);
        ledger.AllBackground.Should().BeTrue();
        ledger.AllLinkedToARun.Should().BeTrue();
        (await host.ReadCallLedgerAsync(unselected.AccountKey)).Count.Should().Be(0);
    }

    [Fact]
    public async Task A_temporarily_failed_scheduled_run_is_retried_once_four_hours_later_and_not_before()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        scenario.FailOnPage(1, ProviderErrorKind.Transient, "server_error");
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 10, 29));
        (await host.RunDueAsync()).Should().Be(0);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 10, 30));
        (await host.RunDueAsync()).Should().Be(1);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 22, 0));
        (await host.RunDueAsync()).Should().Be(0);

        var runs = await host.ReadRunsAsync();
        runs.Select(run => (run.Trigger, run.Outcome)).Should().Equal(
            ("scheduled", "failed_transient"),
            ("retry", "succeeded"));
    }

    [Fact]
    public async Task A_rate_limited_run_is_recorded_with_the_provider_code_and_never_retried_the_same_day()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 2);
        scenario.FailOnPage(1, ProviderErrorKind.RateLimited, "rate_limit");
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        foreach (var (hour, minute) in new[] { (6, 31), (10, 31), (12, 0), (23, 59) })
        {
            host.Clock.SetUtcNow(AmsterdamInstant(Monday, hour, minute));
            (await host.RunDueAsync()).Should().Be(0);
        }

        var runs = await host.ReadRunsAsync();
        runs.Should().ContainSingle();
        runs[0].Outcome.Should().Be("failed_rate_limited");
        runs[0].ProviderError.Should().Be("rate_limit");
        scenario.Calls.Count(call => call.Method == nameof(IBankDataProvider.GetTransactionsAsync)).Should().Be(1);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday.AddDays(1), 6, 30));
        (await host.RunDueAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_consent_rejection_fails_the_run_and_marks_the_connection_expired_at_once()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        scenario.FailOnPage(1, ProviderErrorKind.ConsentRejected, "consent_ended");
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        var runs = await host.ReadRunsAsync();
        runs.Should().ContainSingle();
        runs[0].Outcome.Should().Be("failed_consent");
        runs[0].ProviderError.Should().Be("consent_ended");
        (await host.ReadConnectionStatusesAsync()).Should().Equal("provider_expired");

        host.Clock.SetUtcNow(AmsterdamInstant(Monday.AddDays(1), 6, 30));
        (await host.RunDueAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_credential_rejection_is_recorded_and_not_retried_the_same_day()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        scenario.FailOnPage(1, ProviderErrorKind.ProviderAuth, "credentials_refused");
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 18, 0));
        (await host.RunDueAsync()).Should().Be(0);

        var runs = await host.ReadRunsAsync();
        runs.Should().ContainSingle();
        runs[0].Outcome.Should().Be("failed_provider_auth");
        (await host.ReadConnectionStatusesAsync()).Should().Equal("active");
    }

    [Fact]
    public async Task A_run_that_would_exceed_the_call_budget_stops_before_calling_and_applies_nothing()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 2);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30), SmallCallBudget);
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var account = linked.Accounts[0];

        await host.RecordBackgroundCallsAsync(
            account.Id,
            host.Clock.GetUtcNow().AddHours(-20),
            host.Clock.GetUtcNow().AddHours(-10),
            host.Clock.GetUtcNow().AddHours(-1));

        (await host.RunDueAsync()).Should().Be(1);

        var runs = await host.ReadRunsAsync();
        runs.Should().ContainSingle();
        runs[0].Outcome.Should().Be("quota_exhausted");
        scenario.Calls.Count(call => call.Method == nameof(IBankDataProvider.GetTransactionsAsync)).Should().Be(1);
        (await host.ReadCallLedgerAsync(account.AccountKey)).Count.Should().Be(4);
        (await host.ReadTransactionCountAsync(account.AccountKey)).Should().Be(0);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 18, 0));
        (await host.RunDueAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_call_made_exactly_twenty_four_hours_ago_no_longer_counts_against_the_budget()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30), SmallCallBudget);
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var now = host.Clock.GetUtcNow();

        await host.RecordBackgroundCallsAsync(
            linked.Accounts[0].Id,
            now.AddHours(-24),
            now.AddHours(-2),
            now.AddHours(-1));

        (await host.RunDueAsync()).Should().Be(1);

        var runs = await host.ReadRunsAsync();
        runs.Single().Outcome.Should().Be("succeeded");
    }

    [Fact]
    public async Task A_scheduled_run_is_not_started_for_a_revoked_or_superseded_connection_and_spends_no_calls()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        await host.MarkConnectionAsync(linked.Id, ConnectionStatus.Revoked);
        (await host.RunDueAsync()).Should().Be(0);

        await host.MarkConnectionAsync(linked.Id, ConnectionStatus.Superseded);
        (await host.RunDueAsync()).Should().Be(0);

        (await host.ReadRunsAsync()).Should().BeEmpty();
        scenario.Calls.Count(call => call.Method == nameof(IBankDataProvider.GetTransactionsAsync)).Should().Be(0);
        (await host.ReadCallLedgerAsync(linked.Accounts[0].AccountKey)).Count.Should().Be(0);
    }

    [Fact]
    public async Task A_connection_whose_consent_has_ended_is_left_alone_without_spending_a_call()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        scenario.SessionValidUntil = AmsterdamInstant(Monday, 6, 0);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(0);
        scenario.Calls.Count(call => call.Method == nameof(IBankDataProvider.GetTransactionsAsync)).Should().Be(0);
    }

    [Fact]
    public async Task A_run_left_unfinished_by_a_restart_is_marked_abandoned_when_the_service_starts_even_with_the_scheduler_off()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        var start = AmsterdamInstant(Monday, 5, 0);
        Guid runId;
        string databaseName;

        await using (var first = await SchedulerTestHost.StartAsync(fixture, scenario, start))
        {
            databaseName = first.DatabaseName;
            var linked = await first.LinkAsync(selectFirstAccountOnly: true);
            runId = await first.StartUnfinishedRunAsync(linked.Id, SyncTrigger.Manual);
        }

        await using var second = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            start.AddMinutes(10),
            new Dictionary<string, string?> { ["Ingestion:SchedulerEnabled"] = "false" },
            databaseName);

        var abandoned = await second.WaitForRunOutcomeAsync(runId);

        abandoned.Should().Be("abandoned");
    }

    [Fact]
    public async Task An_abandoned_scheduled_run_counts_as_a_transient_failure_for_the_days_retry()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        await host.StartUnfinishedRunAsync(linked.Id, SyncTrigger.Scheduled);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.AbandonOrphanedRunsAsync();

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 10, 34));
        (await host.RunDueAsync()).Should().Be(0);

        host.Clock.SetUtcNow(AmsterdamInstant(Monday, 10, 35));
        (await host.RunDueAsync()).Should().Be(1);

        var runs = await host.ReadRunsAsync();
        runs.Select(run => (run.Trigger, run.Outcome)).Should().Equal(
            ("scheduled", "abandoned"),
            ("retry", "succeeded"));
    }

    [Fact]
    public async Task Waiting_for_runs_that_never_finish_fails_with_a_descriptive_timeout_instead_of_returning_early()
    {
        await using var host = await SchedulerTestHost.StartAsync(fixture, SyntheticScenario(pagesForFirstAccount: 1), AmsterdamInstant(Monday, 14, 0));

        var act = () => host.WaitForFinishedRunsAsync(1, TimeSpan.FromMilliseconds(300));

        var timeout = await act.Should().ThrowAsync<TimeoutException>();
        timeout.Which.Message.Should().Contain("1 finished sync runs").And.Contain("0 finished and 0 unfinished runs");
    }

    [Fact]
    public async Task Sync_now_queues_an_attended_manual_run_whose_calls_are_not_background_calls()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 2);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("status").GetString().Should().Be("queued");

        var runs = await host.WaitForFinishedRunsAsync(1);
        runs.Should().ContainSingle();
        runs[0].Trigger.Should().Be("manual");
        runs[0].Outcome.Should().Be("succeeded");

        var ledger = await host.ReadCallLedgerAsync(linked.Accounts[0].AccountKey);
        ledger.Count.Should().Be(3);
        ledger.NoneBackground.Should().BeTrue();
    }

    [Theory]
    [InlineData(3, HttpStatusCode.TooManyRequests)]
    [InlineData(2, HttpStatusCode.Accepted)]
    [InlineData(0, HttpStatusCode.Accepted)]
    public async Task Without_the_operators_presence_sync_now_is_refused_when_one_or_fewer_background_calls_are_left(
        int backgroundCallsAlreadyMade,
        HttpStatusCode expected)
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            AmsterdamInstant(Monday, 14, 0),
            new Dictionary<string, string?>
            {
                ["Ingestion:PsuHeadersOnOperatorSyncs"] = "false",
                ["Ingestion:BackgroundCallsPerDay"] = "4"
            });
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var now = host.Clock.GetUtcNow();

        await host.RecordBackgroundCallsAsync(
            linked.Accounts[0].Id,
            Enumerable.Range(1, backgroundCallsAlreadyMade).Select(index => now.AddHours(-index)).ToArray());

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        response.StatusCode.Should().Be(expected);

        if (expected == HttpStatusCode.TooManyRequests)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            problem.GetProperty("title").GetString().Should().Contain("last remaining bank call");
            (await host.ReadRunsAsync()).Should().BeEmpty();
        }
        else
        {
            var runs = await host.WaitForFinishedRunsAsync(1);
            runs.Single().Outcome.Should().Be("succeeded");
        }
    }

    [Fact]
    public async Task Sync_now_hands_the_operators_presence_to_the_queued_request_only_when_the_option_allows_it()
    {
        var withPresence = new RecordingSyncDispatcher();
        await using var attended = await SchedulerTestHost.StartAsync(
            fixture,
            SyntheticScenario(pagesForFirstAccount: 1),
            AmsterdamInstant(Monday, 14, 0),
            configureServices: services => services.AddSingleton<ISyncDispatcher>(withPresence));
        await attended.LinkAsync(selectFirstAccountOnly: true);

        using var attendedResponse = await attended.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        attendedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var request = withPresence.Requests.Should().ContainSingle().Which;
        request.Trigger.Should().Be(SyncTrigger.Manual);
        request.Context.Psu.Should().NotBeNull();
        request.Context.Psu!.UserAgent.Should().Be("ledger-scheduler-tests/1.0");

        var withoutPresence = new RecordingSyncDispatcher();
        await using var unattended = await SchedulerTestHost.StartAsync(
            fixture,
            SyntheticScenario(pagesForFirstAccount: 1),
            AmsterdamInstant(Monday, 14, 0),
            new Dictionary<string, string?> { ["Ingestion:PsuHeadersOnOperatorSyncs"] = "false" },
            configureServices: services => services.AddSingleton<ISyncDispatcher>(withoutPresence));
        await unattended.LinkAsync(selectFirstAccountOnly: true);

        using var unattendedResponse = await unattended.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        unattendedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        withoutPresence.Requests.Should().ContainSingle().Which.Context.Psu.Should().BeNull();
    }

    [Fact]
    public async Task Sync_now_answers_409_when_no_account_is_selected()
    {
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            SyntheticScenario(pagesForFirstAccount: 1),
            AmsterdamInstant(Monday, 14, 0));
        await host.LinkAsync(selectFirstAccountOnly: false, selectAnyAccount: false);

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().Should().Contain("No accounts are selected");
    }

    [Fact]
    public async Task Sync_now_answers_409_when_a_sync_is_already_running()
    {
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            SyntheticScenario(pagesForFirstAccount: 1),
            AmsterdamInstant(Monday, 14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        await host.StartUnfinishedRunAsync(linked.Id, SyncTrigger.Scheduled);

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().Should().Contain("sync is running");
    }

    [Fact]
    public async Task Sync_now_answers_409_when_there_is_no_connection_and_401_without_a_key()
    {
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            SyntheticScenario(pagesForFirstAccount: 1),
            AmsterdamInstant(Monday, 14, 0));

        using var withKey = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync");
        using var withoutKey = await host.SendAsync(HttpMethod.Post, "/api/v1/bank/sync", withKey: false);

        withKey.StatusCode.Should().Be(HttpStatusCode.Conflict);
        withoutKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Nothing_interactive_queues_or_runs_a_sync()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 2);
        var recorder = new RecordingSyncDispatcher();
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            AmsterdamInstant(Monday, 14, 0),
            configureServices: services => services.AddSingleton<ISyncDispatcher>(recorder));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        using var connections = await host.SendAsync(HttpMethod.Get, "/api/v1/bank/connections");
        using var accounts = await host.SendAsync(HttpMethod.Get, $"/api/v1/bank/connections/{linked.ConnectionKey}/accounts");
        using var ops = host.Factory.CreateOpsClient();
        using var health = await ops.GetAsync("/health", TestContext.Current.CancellationToken);
        using var metrics = await ops.GetAsync("/metrics", TestContext.Current.CancellationToken);

        connections.StatusCode.Should().Be(HttpStatusCode.OK);
        accounts.StatusCode.Should().Be(HttpStatusCode.OK);
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        metrics.StatusCode.Should().Be(HttpStatusCode.OK);

        recorder.Requests.Should().BeEmpty();
        (await host.ReadRunsAsync()).Should().BeEmpty();
        scenario.Calls
            .Where(call => call.Method is nameof(IBankDataProvider.GetTransactionsAsync) or nameof(IBankDataProvider.GetBalancesAsync))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_without_a_selected_account_warns_within_minutes_and_once_more_when_the_history_window_has_closed()
    {
        var scenario = SyntheticScenario(pagesForFirstAccount: 1);
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 5, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: false, selectAnyAccount: false);

        host.Clock.Advance(TimeSpan.FromMinutes(4));
        await host.RunDueAsync();
        host.WarningsNaming(linked.ConnectionKey).Should().BeEmpty();

        host.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.RunDueAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.RunDueAsync();
        host.WarningsNaming(linked.ConnectionKey).Should().ContainSingle().Which.Should().Contain("about an hour");

        host.Clock.Advance(TimeSpan.FromMinutes(33));
        await host.RunDueAsync();
        host.WarningsNaming(linked.ConnectionKey).Should().ContainSingle();

        for (var tick = 0; tick < 3; tick++)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(3));
            await host.RunDueAsync();
        }

        var warnings = host.WarningsNaming(linked.ConnectionKey);
        warnings.Should().HaveCount(2);
        warnings[1].Should().Contain("renew the connection");
        (await host.ReadRunsAsync()).Should().BeEmpty();
    }

    private static DateTimeOffset AmsterdamInstant(DateOnly date, int hour, int minute)
    {
        return Ledger.Domain.Ingestion.SyncSchedule.InstantFor(date, new TimeOnly(hour, minute), Amsterdam);
    }

    private static SyntheticBankScenario SyntheticScenario(int pagesForFirstAccount)
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 1;

        var joint = scenario.AddAccount(AccountKind.Current);
        scenario.AddAccount(AccountKind.Savings);

        for (var index = 0; index < pagesForFirstAccount; index++)
        {
            scenario.AddTransaction(
                joint,
                IngestionTestSupport.Booked($"entry-{index:000}", -(index + 1) * 1.5m, BookingDay.AddDays(index)));
        }

        return scenario;
    }
}

/// <summary>A running host over its own fresh database, a controllable clock and the synthetic provider, for scheduler tests.</summary>
public sealed class SchedulerTestHost : IAsyncDisposable
{
    private readonly DatabaseFixture _fixture;
    private string? _token;

    private SchedulerTestHost(
        DatabaseFixture fixture,
        string databaseName,
        LedgerWebApplicationFactory factory,
        FakeTimeProvider clock,
        SyntheticBankScenario scenario)
    {
        _fixture = fixture;
        DatabaseName = databaseName;
        Factory = factory;
        Clock = clock;
        Scenario = scenario;
    }

    /// <summary>The name of the database this host runs against.</summary>
    public string DatabaseName { get; }

    /// <summary>The running host.</summary>
    public LedgerWebApplicationFactory Factory { get; }

    /// <summary>The clock every scheduling and budgeting decision reads.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>The scripted bank behind the host's provider.</summary>
    public SyntheticBankScenario Scenario { get; }

    /// <summary>Boots a host against a new, migrated database, or against an existing one when its name is given.</summary>
    public static async Task<SchedulerTestHost> StartAsync(
        DatabaseFixture fixture,
        SyntheticBankScenario scenario,
        DateTimeOffset start,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
        string? existingDatabase = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var databaseName = existingDatabase ?? await CreateMigratedDatabaseAsync(fixture);
        var clock = new FakeTimeProvider(start);

        var configuration = new Dictionary<string, string?> { ["BankLink:RedirectUrl"] = BankLinkTestHost.RedirectUrl };

        if (additionalConfiguration is not null)
        {
            foreach (var pair in additionalConfiguration)
            {
                configuration[pair.Key] = pair.Value;
            }
        }

        var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringForDatabase(databaseName, "ledger_runtime"),
            configureTestServices: services =>
            {
                services.AddSingleton<IBankDataProvider>(new SyntheticBankDataProvider(scenario));
                services.AddSingleton<TimeProvider>(clock);
                configureServices?.Invoke(services);
            },
            additionalConfiguration: configuration);

        return new SchedulerTestHost(fixture, databaseName, factory, clock, scenario);
    }

    /// <summary>Runs one scheduler pass at the clock's current instant and returns how many runs it started.</summary>
    public async Task<int> RunDueAsync()
    {
        var scheduler = Factory.Services.GetRequiredService<SyncScheduler>();
        return await scheduler.RunDueSyncsAsync(Clock.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stores the scenario's consent as a connection authorised at the clock's current instant and saves the account selection:
    /// every account, or only the first one when requested.
    /// </summary>
    public async Task<LinkedConnection> LinkAsync(bool selectFirstAccountOnly, bool selectAnyAccount = true)
    {
        using var scope = Factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IBankDataProvider>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IBankConnectionStore>();

        var session = await provider.CompleteAuthorizationAsync(Scenario.AuthorizationCode, TestContext.Current.CancellationToken);
        var connection = await store.AddConnectionAsync(
            provider.Name,
            "Synthetic Bank",
            "XX",
            session,
            protector.Protect(session.SessionId),
            Clock.GetUtcNow(),
            TestContext.Current.CancellationToken);

        if (!selectAnyAccount)
        {
            return connection;
        }

        var selections = connection.Accounts
            .Select((account, index) => new AccountSelection(
                account.AccountKey,
                $"Synthetic account {index + 1}",
                !selectFirstAccountOnly || index == 0))
            .ToList();

        await store.SetAccountSelectionAsync(connection.Id, selections, TestContext.Current.CancellationToken);

        var accounts = connection.Accounts
            .Select((account, index) => account with
            {
                DisplayName = selections[index].DisplayName,
                SyncEnabled = selections[index].SyncEnabled
            })
            .ToList();

        return connection with { Accounts = accounts };
    }

    /// <summary>Reads every sync run, oldest first.</summary>
    public async Task<IReadOnlyList<RunRow>> ReadRunsAsync()
    {
        var rows = new List<RunRow>();

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, trigger, outcome, provider_error FROM public.sync_runs ORDER BY started_at, id";

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new RunRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>Summarises the call ledger rows of one account.</summary>
    public async Task<CallLedgerSummary> ReadCallLedgerAsync(string accountKey)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*),
                   count(*) FILTER (WHERE c.background),
                   count(*) FILTER (WHERE c.sync_run_id IS NOT NULL)
            FROM public.provider_calls c
            JOIN public.accounts a ON a.id = c.account_id
            WHERE a.account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        return new CallLedgerSummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>Returns the logged messages that name the connection key and complain about a missing account selection.</summary>
    public IReadOnlyList<string> WarningsNaming(string connectionKey)
    {
        return Factory.CapturedLogMessages
            .Where(message => message.Contains(connectionKey, StringComparison.Ordinal)
                && message.Contains("no selected account", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Sends a request to the API port with a fresh key of the database unless told otherwise, and a User-Agent.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        bool withKey = true,
        string? userAgent = "ledger-scheduler-tests/1.0")
    {
        _token ??= await CreateApiKeyAsync();

        using var client = Factory.CreateApiClient();
        using var request = new HttpRequestMessage(method, path);

        if (withKey)
        {
            request.Headers.Add("X-Api-Key", _token);
        }

        if (userAgent is not null)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Waits until at least the expected number of runs have finished and none is still running, then returns every run, so the
    /// caller sees the settled state. Throws a <see cref="TimeoutException"/> when that never happens.
    /// </summary>
    public async Task<IReadOnlyList<RunRow>> WaitForFinishedRunsAsync(int expected, TimeSpan? timeout = null)
    {
        return await Wait.UntilAsync(
            ReadRunsAsync,
            runs => runs.Count(run => run.Outcome is not null) >= expected && runs.All(run => run.Outcome is not null),
            $"{expected} finished sync runs and none still running",
            runs => $"{runs.Count(run => run.Outcome is not null)} finished and {runs.Count(run => run.Outcome is null)} unfinished runs",
            timeout);
    }

    /// <summary>Reads the stored status of every connection, oldest first.</summary>
    public async Task<IReadOnlyList<string>> ReadConnectionStatusesAsync()
    {
        var statuses = new List<string>();

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM public.bank_connections ORDER BY created_at, id";

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    /// <summary>Counts the stored transactions of one account.</summary>
    public async Task<long> ReadTransactionCountAsync(string accountKey)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM public.transactions t
            JOIN public.accounts a ON a.id = t.account_id
            WHERE a.account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Appends background calls for the account to the call ledger at the given instants.</summary>
    public async Task RecordBackgroundCallsAsync(Guid accountId, params DateTimeOffset[] calledAt)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCallStore>();

        foreach (var instant in calledAt)
        {
            await store.RecordAsync(
                new ProviderCallRecord(accountId, null, instant, ProviderCallKind.Transactions, true),
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Sets the stored status of a connection.</summary>
    public async Task MarkConnectionAsync(Guid connectionId, ConnectionStatus status)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBankConnectionStore>();

        await store.MarkStatusAsync(connectionId, status, Clock.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a run that is never finished, as a process that stopped mid-sync would leave behind.</summary>
    public async Task<Guid> StartUnfinishedRunAsync(Guid connectionId, SyncTrigger trigger)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISyncRunStore>();

        return await store.StartAsync(connectionId, trigger, Clock.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    /// <summary>Marks unfinished runs as abandoned the way the service does when it starts.</summary>
    public async Task AbandonOrphanedRunsAsync()
    {
        var recovery = Factory.Services.GetRequiredService<OrphanedRunRecovery>();
        await recovery.AbandonAsync(Clock.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for a run to have an outcome and returns it; throws a <see cref="TimeoutException"/> when it never gets one.</summary>
    public async Task<string> WaitForRunOutcomeAsync(Guid runId, TimeSpan? timeout = null)
    {
        var run = await Wait.UntilAsync(
            async () => (await ReadRunsAsync()).Single(candidate => candidate.Id == runId),
            candidate => candidate.Outcome is not null,
            "a sync run to record an outcome",
            candidate => "no outcome yet",
            timeout);

        return run.Outcome!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();

        foreach (var role in new[] { "ledger_runtime", "ledger_backup" })
        {
            await using var connection = new NpgsqlConnection(_fixture.ConnectionStringForDatabase(DatabaseName, role));
            NpgsqlConnection.ClearPool(connection);
        }
    }

    private async Task<string> CreateApiKeyAsync()
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(_fixture.ConnectionStringForDatabase(DatabaseName, "ledger_runtime"));

        await using var context = new LedgerDbContext(optionsBuilder.Options);
        var created = await new ApiKeyStore(context).CreateAsync(
            "sched-" + Guid.NewGuid().ToString("N")[..12],
            TestContext.Current.CancellationToken);

        return created.Token;
    }

    private static async Task<string> CreateMigratedDatabaseAsync(DatabaseFixture fixture)
    {
        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        await fixture.MigrateAsync(databaseName);
        return databaseName;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionStringForDatabase(DatabaseName, "ledger_backup"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}

/// <summary>A dispatcher that remembers what was asked of it and runs nothing.</summary>
public sealed class RecordingSyncDispatcher : ISyncDispatcher
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<SyncRequest> _requests = new();

    /// <summary>Every request queued so far.</summary>
    public IReadOnlyList<SyncRequest> Requests => _requests.ToArray();

    /// <inheritdoc />
    public EnqueueResult TryEnqueue(SyncRequest request)
    {
        _requests.Enqueue(request);
        return EnqueueResult.Queued;
    }
}

/// <summary>A stored sync run as the scheduler tests need it.</summary>
public record RunRow(Guid Id, string Trigger, string? Outcome, string? ProviderError);

/// <summary>How many calls the ledger holds for an account and whether they were all background calls tied to a run.</summary>
public record CallLedgerSummary(long Count, long BackgroundCount, long LinkedToRunCount)
{
    /// <summary>Whether every call was a background call.</summary>
    public bool AllBackground => BackgroundCount == Count;

    /// <summary>Whether no call was a background call.</summary>
    public bool NoneBackground => BackgroundCount == 0;

    /// <summary>Whether every call belongs to a sync run.</summary>
    public bool AllLinkedToARun => LinkedToRunCount == Count;
}
