using System.Globalization;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.IntegrationTests.Ingestion;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Metrics;

/// <summary>
/// Proves that consent, sync, allowance, reconciliation and flag state reaches /metrics as a projection of the database with
/// opaque labels only. Every test has its own database and clock because the metrics read every connection in the database and
/// the metric registry is shared by the whole process.
/// </summary>
[Collection("Database")]
[Trait("Category", "Metrics")]
public class SyncMetricsTests(DatabaseFixture fixture)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly Tuesday = new(2026, 10, 27);
    private static readonly DateOnly Wednesday = new(2026, 10, 28);
    private static readonly DateOnly BookingDay = new(2026, 10, 20);
    private static readonly string[] Reasons = ["transient", "rate_limited", "consent_rejected", "provider_auth"];

    [Fact]
    public async Task A_linked_and_synced_connection_is_scraped_with_opaque_keys_for_selected_accounts_only()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        await using var host = await StartAsync(scenario, At(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        (await host.RunDueAsync()).Should().Be(1);

        await RefreshAsync(host);
        var scrape = await ScrapeAsync(host);

        var connectionKey = linked.ConnectionKey;
        var selectedKey = linked.Accounts[0].AccountKey;
        var unselectedKey = linked.Accounts[1].AccountKey;

        ValueOf(scrape, $"ledger_bank_consent_days_until_expiry{{connection=\"{connectionKey}\"}}").Should().BeApproximately(90, 0.01);
        ValueOf(scrape, $"ledger_bank_consent_state{{connection=\"{connectionKey}\",state=\"linked\"}}").Should().Be(1);
        ValueOf(scrape, $"ledger_bank_consent_state{{connection=\"{connectionKey}\",state=\"expiring\"}}").Should().Be(0);
        ValueOf(scrape, $"ledger_bank_consent_state{{connection=\"{connectionKey}\",state=\"expired\"}}").Should().Be(0);
        ValueOf(scrape, $"ledger_sync_last_success_timestamp_seconds{{account=\"{selectedKey}\"}}")
            .Should().BeApproximately(At(Monday, 6, 30).ToUnixTimeSeconds(), 5);
        ValueOf(scrape, $"ledger_sync_calls_remaining{{account=\"{selectedKey}\"}}").Should().Be(998);
        ValueOf(scrape, $"ledger_balance_reconciliation_drift{{account=\"{selectedKey}\"}}").Should().Be(0);
        ValueOf(scrape, $"ledger_transactions_flagged{{account=\"{selectedKey}\"}}").Should().Be(0);

        foreach (var reason in Reasons)
        {
            ValueOf(scrape, $"ledger_sync_errors_total{{reason=\"{reason}\"}}").Should().NotBeNull();
            ValueOf(scrape, $"ledger_sync_failing{{connection=\"{connectionKey}\",reason=\"{reason}\"}}").Should().Be(0);
        }

        scrape.Should().NotContain(unselectedKey);
    }

    [Fact]
    public async Task The_scrape_never_carries_an_account_name_iban_provider_name_or_counterparty()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        await using var host = await StartAsync(scenario, At(Monday, 6, 30));
        await host.LinkAsync(selectFirstAccountOnly: false);
        (await host.RunDueAsync()).Should().Be(1);

        await RefreshAsync(host);
        var scrape = await ScrapeAsync(host);

        scrape.Should().Contain("ledger_sync_last_success_timestamp_seconds{");
        scrape.Should().NotContain(DistinctiveCounterparty);
        scrape.Should().NotContain(DistinctiveDescription);
        scrape.Should().NotContain("Synthetic account 1");
        scrape.Should().NotContain("Synthetic account 2");
        scrape.Should().NotContain("Synthetic Bank");

        foreach (var account in scenario.Accounts)
        {
            scrape.Should().NotContain(account.Iban);
            scrape.Should().NotContain(account.Name);
        }
    }

    [Fact]
    public async Task A_new_host_on_the_same_database_reports_the_same_last_success_without_a_new_sync()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        string databaseName;
        string selectedKey;
        double firstValue;

        await using (var first = await StartAsync(scenario, At(Monday, 6, 30)))
        {
            databaseName = first.DatabaseName;
            var linked = await first.LinkAsync(selectFirstAccountOnly: true);
            selectedKey = linked.Accounts[0].AccountKey;
            (await first.RunDueAsync()).Should().Be(1);
            await RefreshAsync(first);
            firstValue = ValueOf(await ScrapeAsync(first), $"ledger_sync_last_success_timestamp_seconds{{account=\"{selectedKey}\"}}")!.Value;
        }

        await using (var unrelated = await StartAsync(Scenario(sessionEndsAfterDays: 90), At(Monday, 7, 0)))
        {
            await RefreshAsync(unrelated);
            (await ScrapeAsync(unrelated)).Should().NotContain(selectedKey);
        }

        var callsBefore = scenario.Calls.Count;

        await using var restarted = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            At(Monday, 9, 0),
            existingDatabase: databaseName);
        await RefreshAsync(restarted);

        var scrape = await ScrapeAsync(restarted);
        ValueOf(scrape, $"ledger_sync_last_success_timestamp_seconds{{account=\"{selectedKey}\"}}").Should().Be(firstValue);
        scenario.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task A_temporary_failure_is_failing_only_after_the_retry_also_failed_or_had_its_chance()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        scenario.FailOnPage(1, ProviderErrorKind.Transient, "server_error");
        await using var host = await StartAsync(scenario, At(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);
        await RefreshAsync(host);
        var series = $"ledger_sync_failing{{connection=\"{linked.ConnectionKey}\",reason=\"transient\"}}";
        ValueOf(await ScrapeAsync(host), series).Should().Be(0);

        host.Clock.SetUtcNow(At(Monday, 10, 30));
        scenario.FailOnPage(1, ProviderErrorKind.Transient, "server_error");
        (await host.RunDueAsync()).Should().Be(1);
        await RefreshAsync(host);

        var scrape = await ScrapeAsync(host);
        ValueOf(scrape, series).Should().Be(1);
        ValueOf(scrape, "ledger_sync_errors_total{reason=\"transient\"}").Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_rate_limit_is_failing_at_once_and_clears_after_the_next_success()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        scenario.FailOnPage(1, ProviderErrorKind.RateLimited, "rate_limit");
        await using var host = await StartAsync(scenario, At(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var series = $"ledger_sync_failing{{connection=\"{linked.ConnectionKey}\",reason=\"rate_limited\"}}";

        (await host.RunDueAsync()).Should().Be(1);
        await RefreshAsync(host);
        ValueOf(await ScrapeAsync(host), series).Should().Be(1);

        host.Clock.SetUtcNow(At(Tuesday, 6, 30));
        (await host.RunDueAsync()).Should().Be(1);
        await RefreshAsync(host);
        ValueOf(await ScrapeAsync(host), series).Should().Be(0);
    }

    [Fact]
    public async Task A_consent_rejection_is_failing_at_once_and_the_consent_reads_as_expired()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        scenario.FailOnPage(1, ProviderErrorKind.ConsentRejected, "consent_ended");
        await using var host = await StartAsync(scenario, At(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);
        await RefreshAsync(host);

        var scrape = await ScrapeAsync(host);
        ValueOf(scrape, $"ledger_sync_failing{{connection=\"{linked.ConnectionKey}\",reason=\"consent_rejected\"}}").Should().Be(1);
        ValueOf(scrape, $"ledger_bank_consent_state{{connection=\"{linked.ConnectionKey}\",state=\"expired\"}}").Should().Be(1);
        ValueOf(scrape, $"ledger_bank_consent_state{{connection=\"{linked.ConnectionKey}\",state=\"linked\"}}").Should().Be(0);
    }

    [Fact]
    public async Task A_consent_with_less_than_the_warning_period_left_reads_as_expiring_with_fractional_days()
    {
        var scenario = Scenario(sessionEndsAfterDays: 10);
        await using var host = await StartAsync(scenario, At(Monday, 6, 30));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);

        host.Clock.SetUtcNow(At(Monday, 6, 30).AddHours(12));
        await RefreshAsync(host);

        var scrape = await ScrapeAsync(host);
        ValueOf(scrape, $"ledger_bank_consent_state{{connection=\"{linked.ConnectionKey}\",state=\"expiring\"}}").Should().Be(1);
        ValueOf(scrape, $"ledger_bank_consent_days_until_expiry{{connection=\"{linked.ConnectionKey}\"}}").Should().BeApproximately(9.5, 0.01);
    }

    [Fact]
    public async Task A_balance_that_did_not_reconcile_on_two_consecutive_snapshots_raises_the_drift_flag_for_that_account_only()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        var account = scenario.Accounts[0];
        scenario.SetBalances(account, Balances(1000.00m, Monday.AddDays(-1)));
        await using var host = await StartAsync(scenario, At(Monday, 14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var accountKey = linked.Accounts[0].AccountKey;
        var series = $"ledger_balance_reconciliation_drift{{account=\"{accountKey}\"}}";

        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);
        ValueOf(await ScrapeAsync(host), series).Should().Be(0);

        scenario.SetBalances(account, Balances(9999.99m, Monday));
        host.Clock.SetUtcNow(At(Tuesday, 14, 0));
        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);
        ValueOf(await ScrapeAsync(host), series).Should().Be(0);

        scenario.SetBalances(account, Balances(10000.00m, Tuesday));
        host.Clock.SetUtcNow(At(Wednesday, 14, 0));
        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);

        ValueOf(await ScrapeAsync(host), series).Should().Be(1);
    }

    [Fact]
    public async Task A_single_mismatch_followed_by_a_match_never_raises_the_drift_flag()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        var account = scenario.Accounts[0];
        scenario.SetBalances(account, Balances(1000.00m, Monday.AddDays(-1)));
        await using var host = await StartAsync(scenario, At(Monday, 14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var series = $"ledger_balance_reconciliation_drift{{account=\"{linked.Accounts[0].AccountKey}\"}}";

        await SyncAsync(host, linked.Id);

        scenario.SetBalances(account, Balances(9999.99m, Monday));
        host.Clock.SetUtcNow(At(Tuesday, 14, 0));
        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);
        ValueOf(await ScrapeAsync(host), series).Should().Be(0);

        scenario.SetBalances(account, Balances(9999.99m, Tuesday));
        host.Clock.SetUtcNow(At(Wednesday, 14, 0));
        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);

        ValueOf(await ScrapeAsync(host), series).Should().Be(0);
    }

    [Fact]
    public async Task Two_mismatches_weeks_apart_do_not_raise_the_drift_flag_because_the_drift_did_not_persist()
    {
        var scenario = Scenario(sessionEndsAfterDays: 90);
        var account = scenario.Accounts[0];
        scenario.SetBalances(account, Balances(1000.00m, Monday.AddDays(-1)));
        await using var host = await StartAsync(scenario, At(Monday, 14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: true);
        var series = $"ledger_balance_reconciliation_drift{{account=\"{linked.Accounts[0].AccountKey}\"}}";

        await SyncAsync(host, linked.Id);

        scenario.SetBalances(account, Balances(9999.99m, Monday));
        host.Clock.SetUtcNow(At(Tuesday, 14, 0));
        await SyncAsync(host, linked.Id);

        scenario.SetBalances(account, Balances(10000.00m, Tuesday.AddDays(20)));
        host.Clock.SetUtcNow(At(Tuesday.AddDays(21), 14, 0));
        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);

        ValueOf(await ScrapeAsync(host), series).Should().Be(0);
    }

    [Fact]
    public async Task Pending_transactions_with_an_ambiguous_match_are_counted_per_account()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.SessionValidUntil = At(Monday, 6, 30).AddDays(90);
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-pending-one", -20.00m, BookingDay));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-pending-two", -20.00m, BookingDay));

        await using var host = await StartAsync(scenario, At(Monday, 14, 0));
        var linked = await host.LinkAsync(selectFirstAccountOnly: false);
        await SyncAsync(host, linked.Id);

        scenario.Remove(account, 1);
        scenario.Remove(account, 0);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-booked-three", -20.00m, BookingDay.AddDays(1)));
        await SyncAsync(host, linked.Id);
        await RefreshAsync(host);

        ValueOf(await ScrapeAsync(host), $"ledger_transactions_flagged{{account=\"{linked.Accounts[0].AccountKey}\"}}").Should().Be(2);
    }

    private const string DistinctiveCounterparty = "Zq7 Distinctive Counterparty";
    private const string DistinctiveDescription = "Zq7 distinctive description";

    private static DateTimeOffset At(DateOnly day, int hour, int minute)
    {
        return SyncSchedule.InstantFor(day, new TimeOnly(hour, minute), Amsterdam);
    }

    private static SyntheticBankScenario Scenario(int sessionEndsAfterDays)
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.PageSize = 1;
        scenario.SessionValidUntil = At(Monday, 6, 30).AddDays(sessionEndsAfterDays);

        var joint = scenario.AddAccount(AccountKind.Current);
        scenario.AddAccount(AccountKind.Savings);

        scenario.AddTransaction(
            joint,
            IngestionTestSupport.Booked("entry-000", -12.34m, BookingDay, DistinctiveCounterparty, DistinctiveDescription));

        return scenario;
    }

    private static IReadOnlyList<ProviderBalance> Balances(decimal closing, DateOnly referenceDate)
    {
        return [new ProviderBalance(BalanceKind.ClosingBooked, "CLBD", closing, "EUR", referenceDate)];
    }

    private async Task<SchedulerTestHost> StartAsync(SyntheticBankScenario scenario, DateTimeOffset start)
    {
        return await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            start,
            new Dictionary<string, string?> { ["Ingestion:BackgroundCallsPerDay"] = "1000" });
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

    private static async Task RefreshAsync(SchedulerTestHost host)
    {
        await host.Factory.Services
            .GetRequiredService<SyncMetricsRefresher>()
            .RefreshOnceAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> ScrapeAsync(SchedulerTestHost host)
    {
        using var client = host.Factory.CreateOpsClient();
        return await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);
    }

    private static double? ValueOf(string scrape, string series)
    {
        foreach (var line in scrape.Split('\n'))
        {
            if (line.StartsWith(series + " ", StringComparison.Ordinal))
            {
                return double.Parse(line[(series.Length + 1)..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}
