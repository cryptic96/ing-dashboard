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
        ledger.Count.Should().Be(pageCalls);
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
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
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
        await using var host = await SchedulerTestHost.StartAsync(fixture, scenario, AmsterdamInstant(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var now = host.Clock.GetUtcNow();

        await host.RecordBackgroundCallsAsync(
            linked.Accounts[0].Id,
            now.AddHours(-24),
            now.AddHours(-2),
            now.AddHours(-1),
            now.AddMinutes(-30));

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
    public async Task A_run_left_unfinished_by_a_restart_is_marked_abandoned_when_the_scheduler_starts()
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
            new Dictionary<string, string?> { ["Ingestion:SchedulerEnabled"] = "true" },
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
    public async Task<LinkedConnection> LinkAsync(bool selectFirstAccountOnly)
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
            SELECT count(*), COALESCE(bool_and(c.background), true), COALESCE(bool_and(c.sync_run_id IS NOT NULL), true)
            FROM public.provider_calls c
            JOIN public.accounts a ON a.id = c.account_id
            WHERE a.account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        return new CallLedgerSummary(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2));
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

    /// <summary>Marks unfinished runs as abandoned the way the scheduler does when it starts.</summary>
    public async Task AbandonOrphanedRunsAsync()
    {
        var scheduler = Factory.Services.GetRequiredService<SyncScheduler>();
        await scheduler.AbandonOrphanedRunsAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits up to ten seconds for a run to have an outcome and returns it, or null when it never gets one.</summary>
    public async Task<string?> WaitForRunOutcomeAsync(Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = (await ReadRunsAsync()).Single(candidate => candidate.Id == runId);

            if (run.Outcome is not null)
            {
                return run.Outcome;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
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

/// <summary>A stored sync run as the scheduler tests need it.</summary>
public record RunRow(Guid Id, string Trigger, string? Outcome, string? ProviderError);

/// <summary>How many calls the ledger holds for an account and whether they were all background calls tied to a run.</summary>
public record CallLedgerSummary(long Count, bool AllBackground, bool AllLinkedToARun);
