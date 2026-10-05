using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Proves the daily balance snapshot and the to-the-cent reconciliation against the booked transactions on real PostgreSQL.
/// Every test gets its own database and a controllable clock so days can be stepped through.
/// </summary>
[Collection("Database")]
[Trait("Category", "Balances")]
public class BalanceSnapshotTests(DatabaseFixture fixture)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateOnly DayOne = new(2026, 10, 26);
    private static readonly DateOnly DayTwo = new(2026, 10, 27);
    private static readonly DateOnly Yesterday = new(2026, 10, 25);

    private static readonly IReadOnlyDictionary<string, string?> UndatedConfiguration = new Dictionary<string, string?>
    {
        ["Ingestion:ReconcileBalanceKinds:0"] = "Expected",
        ["Ingestion:ReconcileUndatedBalances"] = "true"
    };

    [Fact]
    public async Task The_first_sync_of_a_day_stores_every_returned_balance_as_an_unverified_baseline_and_a_second_sync_reads_none()
    {
        var (scenario, _) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);

        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.Succeeded);

        var snapshots = await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey);
        snapshots.Select(snapshot => snapshot.Kind).Should().BeEquivalentTo("closing_booked", "interim_available");
        snapshots.Should().OnlyContain(snapshot => snapshot.SnapshotDate == DayOne && snapshot.Reconciled == null);
        snapshots.Single(snapshot => snapshot.Kind == "closing_booked").Amount.Should().Be(1000.00m);
        BalanceCalls(scenario).Should().Be(1);

        host.Clock.Advance(TimeSpan.FromHours(2));
        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.Succeeded);

        BalanceCalls(scenario).Should().Be(1);
        (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_consistent_closing_balance_on_the_next_day_reconciles_to_the_cent()
    {
        var (scenario, account) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        await SyncAsync(host, connection.Id);

        AdvanceToDayTwo(host, scenario, account, closing: 1150.11m);
        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.Succeeded);

        var closing = (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey))
            .Single(snapshot => snapshot.SnapshotDate == DayTwo && snapshot.Kind == "closing_booked");

        closing.Reconciled.Should().BeTrue();
        closing.Expected.Should().Be(1150.11m);
        closing.Drift.Should().Be(0m);
        closing.ReferenceDate.Should().Be(DayOne);
    }

    [Fact]
    public async Task A_closing_balance_one_cent_off_is_recorded_with_the_expected_amount_and_the_drift()
    {
        var (scenario, account) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        await SyncAsync(host, connection.Id);

        AdvanceToDayTwo(host, scenario, account, closing: 1150.12m);
        await SyncAsync(host, connection.Id);

        var closing = (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey))
            .Single(snapshot => snapshot.SnapshotDate == DayTwo && snapshot.Kind == "closing_booked");

        closing.Reconciled.Should().BeFalse();
        closing.Expected.Should().Be(1150.11m);
        closing.Drift.Should().Be(0.01m);

        var others = (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey))
            .Where(snapshot => snapshot.SnapshotDate == DayTwo && snapshot.Kind != "closing_booked");
        others.Should().OnlyContain(snapshot => snapshot.Reconciled == null && snapshot.Drift == null);
    }

    [Fact]
    public async Task Pending_and_dropped_transactions_and_later_bookings_never_enter_the_reconciliation_sum()
    {
        var (scenario, account) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-soon-dropped", -70.00m, DayOne) with { BookingDate = DayOne });
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        await SyncAsync(host, connection.Id);

        scenario.Remove(account, account.Transactions.Count - 1);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-still-pending", -30.00m, DayOne) with { BookingDate = DayOne });
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-after-reference", -400.00m, DayTwo));
        AdvanceToDayTwo(host, scenario, account, closing: 1150.11m);
        await SyncAsync(host, connection.Id);

        var statuses = await ReadTransactionStatusesAsync(host, connection.Accounts[0].AccountKey);
        statuses.Should().Contain(["dropped", "pending", "booked"]);

        var closing = (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey))
            .Single(snapshot => snapshot.SnapshotDate == DayTwo && snapshot.Kind == "closing_booked");
        closing.Reconciled.Should().BeTrue();
        closing.Drift.Should().Be(0m);
    }

    [Fact]
    public async Task A_failed_transaction_fetch_makes_no_balance_call_and_the_next_successful_run_of_the_day_reads_balances()
    {
        var (scenario, _) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        scenario.FailOnPage(1, ProviderErrorKind.Transient, "server_error");
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);

        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.FailedTransient);

        BalanceCalls(scenario).Should().Be(0);
        (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey)).Should().BeEmpty();

        host.Clock.Advance(TimeSpan.FromHours(4));
        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.Succeeded);

        BalanceCalls(scenario).Should().Be(1);
        (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_balance_fetch_fails_the_run_keeps_the_applied_transactions_and_is_retried_the_same_day()
    {
        var (scenario, _) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        var failing = new FailOnceOnBalancesProvider(new SyntheticBankDataProvider(scenario));
        await using var host = await StartAsync(
            scenario,
            DayOne,
            configureServices: services => services.AddSingleton<IBankDataProvider>(failing));
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.FailedTransient);

        (await host.ReadTransactionCountAsync(accountKey)).Should().Be(1);
        (await ReadSnapshotsAsync(host, accountKey)).Should().BeEmpty();

        host.Clock.Advance(TimeSpan.FromHours(4));
        (await SyncAsync(host, connection.Id)).Outcome.Should().Be(SyncOutcome.Succeeded);

        (await ReadSnapshotsAsync(host, accountKey)).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_balances_call_is_written_to_the_call_ledger()
    {
        var (scenario, _) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);

        await SyncAsync(host, connection.Id);

        (await CountCallsAsync(host, connection.Accounts[0].AccountKey, "balances")).Should().Be(1);
        (await CountCallsAsync(host, connection.Accounts[0].AccountKey, "transactions")).Should().Be(1);
    }

    [Fact]
    public async Task With_the_budget_used_up_by_the_transaction_fetch_the_run_stops_before_calling_balances()
    {
        var (scenario, _) = ScenarioWithClosingBalance(1000.00m, Yesterday);
        await using var host = await StartAsync(
            scenario,
            DayOne,
            new Dictionary<string, string?> { ["Ingestion:BackgroundCallsPerDay"] = "1" });
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        var result = await SyncAsync(host, connection.Id);

        result.Outcome.Should().Be(SyncOutcome.QuotaExhausted);
        BalanceCalls(scenario).Should().Be(0);
        (await CountCallsAsync(host, accountKey, "balances")).Should().Be(0);
        (await ReadSnapshotsAsync(host, accountKey)).Should().BeEmpty();
        (await host.ReadTransactionCountAsync(accountKey)).Should().Be(1);
    }

    [Fact]
    public async Task A_missing_booked_balance_type_records_every_snapshot_as_unknown()
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Savings);
        scenario.SetBalances(account, [new ProviderBalance(BalanceKind.InterimAvailable, "ITAV", 50.00m, "EUR", Yesterday)]);
        await using var host = await StartAsync(scenario, DayOne);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        await SyncAsync(host, connection.Id);

        host.Clock.SetUtcNow(InstantFor(DayTwo));
        await SyncAsync(host, connection.Id);

        var snapshots = await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey);
        snapshots.Should().HaveCount(2);
        snapshots.Should().OnlyContain(snapshot => snapshot.Reconciled == null && snapshot.Expected == null);
    }

    [Fact]
    public async Task An_undated_expected_balance_reconciles_to_the_cent_on_the_window_between_the_two_fetches()
    {
        var (scenario, account) = ScenarioWithUndatedBalance(1000.00m);
        await using var host = await StartAsync(scenario, DayOne, UndatedConfiguration);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;
        await SyncAsync(host, connection.Id);

        var baseline = (await ReadSnapshotsAsync(host, accountKey)).Single();
        baseline.Kind.Should().Be("expected");
        baseline.ReferenceDate.Should().BeNull();
        baseline.Reconciled.Should().BeNull();

        AddDayOneBookings(scenario, account);
        scenario.SetBalances(account, UndatedBalances(1150.11m));
        host.Clock.SetUtcNow(InstantFor(DayTwo));
        await SyncAsync(host, connection.Id);

        var expected = (await ReadSnapshotsAsync(host, accountKey)).Single(snapshot => snapshot.SnapshotDate == DayTwo);
        expected.Reconciled.Should().BeTrue();
        expected.Expected.Should().Be(1150.11m);
        expected.Drift.Should().Be(0m);
        expected.ReferenceDate.Should().BeNull();
    }

    [Fact]
    public async Task An_undated_expected_balance_one_cent_off_is_recorded_with_the_exact_drift()
    {
        var (scenario, account) = ScenarioWithUndatedBalance(1000.00m);
        await using var host = await StartAsync(scenario, DayOne, UndatedConfiguration);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;
        await SyncAsync(host, connection.Id);

        AddDayOneBookings(scenario, account);
        scenario.SetBalances(account, UndatedBalances(1150.12m));
        host.Clock.SetUtcNow(InstantFor(DayTwo));
        await SyncAsync(host, connection.Id);

        var expected = (await ReadSnapshotsAsync(host, accountKey)).Single(snapshot => snapshot.SnapshotDate == DayTwo);
        expected.Reconciled.Should().BeFalse();
        expected.Expected.Should().Be(1150.11m);
        expected.Drift.Should().Be(0.01m);
    }

    [Fact]
    public async Task A_transaction_first_booked_after_the_first_fetch_is_counted_in_the_next_window_exactly_once()
    {
        var (scenario, account) = ScenarioWithUndatedBalance(1000.00m);
        await using var host = await StartAsync(scenario, DayOne, UndatedConfiguration);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;
        await SyncAsync(host, connection.Id);

        host.Clock.Advance(TimeSpan.FromHours(3));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-late", -40.00m, DayOne));
        await SyncAsync(host, connection.Id);

        scenario.SetBalances(account, UndatedBalances(960.00m));
        host.Clock.SetUtcNow(InstantFor(DayTwo));
        await SyncAsync(host, connection.Id);

        host.Clock.SetUtcNow(InstantFor(DayTwo.AddDays(1)));
        await SyncAsync(host, connection.Id);

        var snapshots = await ReadSnapshotsAsync(host, accountKey);
        snapshots.Single(snapshot => snapshot.SnapshotDate == DayTwo).Reconciled.Should().BeTrue();
        var dayThree = snapshots.Single(snapshot => snapshot.SnapshotDate == DayTwo.AddDays(1));
        dayThree.Reconciled.Should().BeTrue();
        dayThree.Expected.Should().Be(960.00m);
    }

    [Fact]
    public async Task Pending_and_dropped_transactions_never_enter_the_undated_window()
    {
        var (scenario, account) = ScenarioWithUndatedBalance(1000.00m);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-soon-dropped", -70.00m, DayOne) with { BookingDate = DayOne });
        await using var host = await StartAsync(scenario, DayOne, UndatedConfiguration);
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;
        await SyncAsync(host, connection.Id);

        scenario.Remove(account, account.Transactions.Count - 1);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-still-pending", -30.00m, DayOne) with { BookingDate = DayOne });
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-counted", -10.00m, DayTwo));
        scenario.SetBalances(account, UndatedBalances(990.00m));
        host.Clock.SetUtcNow(InstantFor(DayTwo));
        await SyncAsync(host, connection.Id);

        (await ReadTransactionStatusesAsync(host, accountKey)).Should().Contain(["dropped", "pending", "booked"]);
        var expected = (await ReadSnapshotsAsync(host, accountKey)).Single(snapshot => snapshot.SnapshotDate == DayTwo);
        expected.Reconciled.Should().BeTrue();
        expected.Drift.Should().Be(0m);
    }

    [Fact]
    public async Task Without_the_undated_option_an_undated_expected_balance_stays_unknown()
    {
        var (scenario, account) = ScenarioWithUndatedBalance(1000.00m);
        await using var host = await StartAsync(
            scenario,
            DayOne,
            new Dictionary<string, string?> { ["Ingestion:ReconcileBalanceKinds:0"] = "Expected" });
        var connection = await host.LinkAsync(selectFirstAccountOnly: false);
        await SyncAsync(host, connection.Id);

        AddDayOneBookings(scenario, account);
        scenario.SetBalances(account, UndatedBalances(1150.11m));
        host.Clock.SetUtcNow(InstantFor(DayTwo));
        await SyncAsync(host, connection.Id);

        (await ReadSnapshotsAsync(host, connection.Accounts[0].AccountKey)).Should().OnlyContain(snapshot => snapshot.Reconciled == null);
    }

    private static (SyntheticBankScenario Scenario, SyntheticAccount Account) ScenarioWithUndatedBalance(decimal expected)
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-opening", 500.00m, Yesterday.AddDays(-1)));
        scenario.SetBalances(account, UndatedBalances(expected));
        return (scenario, account);
    }

    private static IReadOnlyList<ProviderBalance> UndatedBalances(decimal expected)
    {
        return [new ProviderBalance(BalanceKind.Expected, "XPCD", expected, "EUR", null)];
    }

    private static void AddDayOneBookings(SyntheticBankScenario scenario, SyntheticAccount account)
    {
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-income", 250.10m, DayOne));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-groceries", -99.99m, DayOne));
    }

    private async Task<SchedulerTestHost> StartAsync(
        SyntheticBankScenario scenario,
        DateOnly day,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var merged = new Dictionary<string, string?> { ["Ingestion:BackgroundCallsPerDay"] = "1000" };

        if (configuration is not null)
        {
            foreach (var pair in configuration)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return await SchedulerTestHost.StartAsync(fixture, scenario, InstantFor(day), merged, configureServices: configureServices);
    }

    private static (SyntheticBankScenario Scenario, SyntheticAccount Account) ScenarioWithClosingBalance(
        decimal closing,
        DateOnly referenceDate)
    {
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-opening", 500.00m, Yesterday.AddDays(-1)));
        scenario.SetBalances(account, Balances(closing, referenceDate));
        return (scenario, account);
    }

    private static IReadOnlyList<ProviderBalance> Balances(decimal closing, DateOnly referenceDate)
    {
        return
        [
            new ProviderBalance(BalanceKind.ClosingBooked, "CLBD", closing, "EUR", referenceDate),
            new ProviderBalance(BalanceKind.InterimAvailable, "ITAV", closing - 100.00m, "EUR", referenceDate.AddDays(1))
        ];
    }

    private static void AdvanceToDayTwo(
        SchedulerTestHost host,
        SyntheticBankScenario scenario,
        SyntheticAccount account,
        decimal closing)
    {
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-income", 250.10m, DayOne));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-groceries", -99.99m, DayOne));
        scenario.SetBalances(account, Balances(closing, DayOne));
        host.Clock.SetUtcNow(InstantFor(DayTwo));
    }

    private static DateTimeOffset InstantFor(DateOnly day)
    {
        return SyncSchedule.InstantFor(day, new TimeOnly(14, 0), Amsterdam);
    }

    private static int BalanceCalls(SyntheticBankScenario scenario)
    {
        return scenario.Calls.Count(call => call.Method == nameof(IBankDataProvider.GetBalancesAsync));
    }

    private static async Task<SyncRunResult> SyncAsync(SchedulerTestHost host, Guid connectionId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();

        return await orchestrator.SyncConnectionAsync(
            connectionId,
            SyncTrigger.Scheduled,
            FetchContext.Background,
            TestContext.Current.CancellationToken);
    }

    private async Task<long> CountCallsAsync(SchedulerTestHost host, string accountKey, string kind)
    {
        await using var connection = await OpenAsync(host);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM public.provider_calls c
            JOIN public.accounts a ON a.id = c.account_id
            WHERE a.account_key = @accountKey AND c.kind = @kind
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);
        command.Parameters.AddWithValue("kind", kind);

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<IReadOnlyList<string>> ReadTransactionStatusesAsync(SchedulerTestHost host, string accountKey)
    {
        await using var connection = await OpenAsync(host);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.status FROM public.transactions t
            JOIN public.accounts a ON a.id = t.account_id
            WHERE a.account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var statuses = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    private async Task<IReadOnlyList<StoredSnapshot>> ReadSnapshotsAsync(SchedulerTestHost host, string accountKey)
    {
        await using var connection = await OpenAsync(host);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.snapshot_date, s.balance_kind, s.amount, s.reference_date, s.reconciled, s.expected_amount, s.drift_amount
            FROM public.balance_snapshots s
            JOIN public.accounts a ON a.id = s.account_id
            WHERE a.account_key = @accountKey
            ORDER BY s.snapshot_date, s.balance_kind
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var rows = new List<StoredSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new StoredSnapshot(
                reader.GetFieldValue<DateOnly>(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6)));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync(SchedulerTestHost host)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(host.DatabaseName, "ledger_backup"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private sealed record StoredSnapshot(
        DateOnly SnapshotDate,
        string Kind,
        decimal Amount,
        DateOnly? ReferenceDate,
        bool? Reconciled,
        decimal? Expected,
        decimal? Drift);

    private sealed class FailOnceOnBalancesProvider(IBankDataProvider inner) : IBankDataProvider
    {
        private bool _failed;

        public string Name => inner.Name;

        public Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
        {
            return inner.StartAuthorizationAsync(request, cancellationToken);
        }

        public Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken)
        {
            return inner.CompleteAuthorizationAsync(code, cancellationToken);
        }

        public Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
            ProviderAccountRef account,
            FetchContext context,
            CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new BankProviderException(ProviderErrorKind.Transient, "server_error", "The synthetic bank reported a failure.");
            }

            return inner.GetBalancesAsync(account, context, cancellationToken);
        }

        public IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
            ProviderAccountRef account,
            TransactionQuery query,
            FetchContext context,
            CancellationToken cancellationToken)
        {
            return inner.GetTransactionsAsync(account, query, context, cancellationToken);
        }

        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            return inner.RevokeSessionAsync(sessionId, cancellationToken);
        }
    }
}
