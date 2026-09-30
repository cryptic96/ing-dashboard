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
