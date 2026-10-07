using System.Text.Json;
using FluentAssertions;
using Ledger.Domain.Queries;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Verifies money totals on a real database seeded with a synthetic August: booked-only sums, pending and own-account transfers
/// reported beside the total, Amsterdam period edges, filters, grouping and provenance.
/// </summary>
[Collection("Database")]
public class TotalsQueryTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private static TotalsRequest August(
        IReadOnlyList<string>? counterparty = null,
        IReadOnlyList<string>? description = null,
        IReadOnlyList<string>? counterpartyRef = null,
        IReadOnlyList<string>? accounts = null,
        string? direction = null,
        decimal? minAmount = null,
        decimal? maxAmount = null,
        string? groupBy = null)
    {
        return new TotalsRequest(
            null,
            "2026-08-01",
            "2026-08-31",
            counterparty,
            counterpartyRef,
            description,
            accounts,
            direction,
            minAmount,
            maxAmount,
            groupBy);
    }

    private async Task<(McpTestHost Host, LedgerQuerySeed.TotalsScenario Scenario)> StartAsync()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        var scenario = await LedgerQuerySeed.SeedTotalsScenarioAsync(connectionString);
        var host = await McpTestHost.StartAsync(
            connectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now)));

        return (host, scenario);
    }

    private static async Task<TotalsResult> TotalsAsync(McpTestHost host, TotalsRequest request)
    {
        using var scope = host.Factory.Services.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<LedgerQueryService>();

        return await queries.TotalsAsync(request, TestContext.Current.CancellationToken);
    }

    private static TotalsCurrency Currency(TotalsResult result, string code)
    {
        return result.Currencies.Single(currency => currency.Currency == code);
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task August_totals_count_booked_rows_only_and_state_what_was_left_out()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var result = await TotalsAsync(host, August());

        var euro = Currency(result, "EUR");
        euro.MoneyOut.Should().Be("268.50");
        euro.MoneyIn.Should().Be("2000.00");
        euro.Net.Should().Be("1731.50");
        euro.TransactionCount.Should().Be(10);
        euro.CounterpartyCount.Should().Be(6);

        var pending = result.PendingNotIncluded.Single(item => item.Currency == "EUR");
        pending.TransactionCount.Should().Be(1);
        pending.MoneyOut.Should().Be("12.50");
        pending.MoneyIn.Should().Be("0.00");

        var transfers = result.InternalTransfersExcluded.Single(item => item.Currency == "EUR");
        transfers.TransactionCount.Should().Be(2);
        transfers.MoneyOut.Should().Be("300.00");
        transfers.MoneyIn.Should().Be("300.00");

        result.Period.From.Should().Be("2026-08-01");
        result.Period.To.Should().Be("2026-08-31");
        result.Period.TimeZone.Should().Be("Europe/Amsterdam");
        result.Basis.Should().Contain("booked").And.Contain("pending reported separately").And.Contain("own synced accounts excluded");
        result.GroupedBy.Should().Be("counterparty name, not category");
        result.DataAsOf.Select(account => account.AccountName).Should().Equal("Joint", "Savings");
        result.DataAsOf.Should().OnlyContain(account => account.LastSuccessfulSync == "2026-10-06T07:30:00Z");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task A_dropped_row_never_appears_in_any_part_of_a_result()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var result = await TotalsAsync(host, August(counterparty: ["example market"]));
        var json = JsonSerializer.Serialize(result);

        Currency(result, "EUR").MoneyOut.Should().Be("65.50");
        json.Should().NotContain("99.00");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task A_transfer_between_synced_accounts_is_excluded_on_both_sides_but_one_to_an_unsynced_own_account_counts()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var result = await TotalsAsync(host, August());
        var names = Currency(result, "EUR").Counterparties.Select(counterparty => counterparty.Name).ToList();

        names.Should().NotContain("Savings").And.NotContain("Joint");
        names.Should().Contain("Other own");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Other_currencies_keep_their_own_entry_and_are_never_added()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var result = await TotalsAsync(host, August());

        result.Currencies.Select(currency => currency.Currency).Should().Equal("EUR", "USD");
        var dollars = Currency(result, "USD");
        dollars.MoneyOut.Should().Be("10.00");
        dollars.TransactionCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Two_rows_identical_in_every_field_are_both_counted()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var result = await TotalsAsync(host, August(description: ["small purchase"]));

        var euro = Currency(result, "EUR");
        euro.TransactionCount.Should().Be(2);
        euro.MoneyOut.Should().Be("10.00");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task A_counterparty_filter_matches_spellings_that_differ_in_case_and_spacing_and_a_description_term_narrows_it()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var both = Currency(await TotalsAsync(host, August(counterparty: ["example market"])), "EUR");
        both.TransactionCount.Should().Be(2);
        both.MoneyOut.Should().Be("65.50");
        both.Counterparties.Should().ContainSingle();

        var narrowed = Currency(await TotalsAsync(host, August(counterparty: ["example market"], description: ["bags"])), "EUR");
        narrowed.TransactionCount.Should().Be(1);
        narrowed.MoneyOut.Should().Be("25.50");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Terms_within_one_list_are_alternatives_and_wildcards_in_a_term_are_matched_literally()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var alternatives = Currency(await TotalsAsync(host, August(counterparty: ["bakery", "employer"])), "EUR");
        alternatives.TransactionCount.Should().Be(2);

        var wildcard = await TotalsAsync(host, August(counterparty: ["%"]));
        Currency(wildcard, "EUR").TransactionCount.Should().Be(0);
        Currency(wildcard, "EUR").MoneyOut.Should().Be("0.00");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task An_account_key_filter_limits_the_totals_to_that_account()
    {
        var (host, scenario) = await StartAsync();
        await using var running = host;

        var savings = Currency(await TotalsAsync(host, August(accounts: [scenario.SavingsKey])), "EUR");
        savings.TransactionCount.Should().Be(0);
        savings.MoneyIn.Should().Be("0.00");

        var joint = Currency(await TotalsAsync(host, August(accounts: [scenario.JointKey])), "EUR");
        joint.TransactionCount.Should().Be(10);
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task An_unknown_account_key_is_refused()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var act = () => TotalsAsync(host, August(accounts: ["0000000000000000"]));

        await act.Should().ThrowAsync<LedgerQueryException>();
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Direction_out_leaves_money_in_at_zero()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var euro = Currency(await TotalsAsync(host, August(direction: "out")), "EUR");

        euro.MoneyIn.Should().Be("0.00");
        euro.MoneyOut.Should().Be("268.50");
        euro.TransactionCount.Should().Be(9);
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Amount_bounds_are_inclusive_on_the_absolute_amount()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var euro = Currency(await TotalsAsync(host, August(direction: "out", minAmount: 30m, maxAmount: 40m)), "EUR");

        euro.TransactionCount.Should().Be(2);
        euro.MoneyOut.Should().Be("70.00");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Explicit_end_dates_are_inclusive_and_a_day_outside_does_not_count()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var request = new TotalsRequest(null, "2026-08-31", "2026-08-31", null, null, null, null, null, null, null, null);
        var result = await TotalsAsync(host, request);

        var euro = Currency(result, "EUR");
        euro.MoneyOut.Should().Be("11.00");
        euro.TransactionCount.Should().Be(2);
        result.PendingNotIncluded.Single(item => item.Currency == "EUR").TransactionCount.Should().Be(1);

        var september = await TotalsAsync(host, new TotalsRequest(null, "2026-09-01", "2026-09-01", null, null, null, null, null, null, null, null));
        Currency(september, "EUR").MoneyOut.Should().Be("9.00");
        september.PendingNotIncluded.Single(item => item.Currency == "EUR").MoneyOut.Should().Be("7.00");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task Rows_without_a_booking_date_fall_back_to_the_transaction_date_and_then_the_amsterdam_first_seen_day()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var thirtieth = new TotalsRequest(null, "2026-08-30", "2026-08-30", ["cafe"], null, null, null, null, null, null, null);
        Currency(await TotalsAsync(host, thirtieth), "EUR").MoneyOut.Should().Be("2.00");

        var thirtyFirst = new TotalsRequest(null, "2026-08-31", "2026-08-31", ["cafe"], null, null, null, null, null, null, null);
        Currency(await TotalsAsync(host, thirtyFirst), "EUR").MoneyOut.Should().Be("3.00");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task The_summary_names_the_period_the_count_and_the_exclusions()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var result = await TotalsAsync(host, August());

        result.Summary.Should().Contain("268.50 EUR out, 2000.00 EUR in, net 1731.50 EUR across 10 booked transactions at 6 counterparties");
        result.Summary.Should().Contain("2026-08-01 to 2026-08-31 (Europe/Amsterdam)");
        result.Summary.Should().Contain("Not included: 1 pending transaction (12.50 EUR out) and 2 transfers between the household's own accounts.");
        result.Summary.Should().Contain("Grouped by counterparty name, not by category.");
        result.Summary.Should().Contain("Data as of 2026-10-06T07:30:00Z.");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task A_result_never_carries_an_account_number_or_a_provider_account_name()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var json = JsonSerializer.Serialize(await TotalsAsync(host, August(groupBy: "account")));

        json.Should().NotContain("XX00").And.NotContain("xx00").And.NotContain(LedgerQuerySeed.JointProviderName);
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task The_money_totals_tool_returns_the_same_figures_as_text_through_the_mcp_client()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var result = await connection.Client.CallToolAsync(
            "money_totals",
            new Dictionary<string, object?>
            {
                ["fromDate"] = "2026-08-01",
                ["toDate"] = "2026-08-31",
                ["counterparty"] = new[] { "example market" }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        using var document = JsonDocument.Parse(text);
        var euro = document.RootElement.GetProperty("currencies").EnumerateArray()
            .Single(currency => currency.GetProperty("currency").GetString() == "EUR");

        euro.GetProperty("money_out").GetString().Should().Be("65.50");
        euro.GetProperty("money_in").GetString().Should().Be("0.00");
        euro.GetProperty("net").GetString().Should().Be("-65.50");
        euro.GetProperty("transaction_count").GetInt32().Should().Be(2);

        var summary = document.RootElement.GetProperty("summary").GetString();
        summary.Should().Contain("2026-08-01 to 2026-08-31").And.Contain("2 booked transactions").And.Contain("Not included");
    }

    [Fact]
    [Trait("Category", "Totals")]
    public async Task A_refused_request_reaches_the_client_as_a_plain_message()
    {
        var (host, _) = await StartAsync();
        await using var running = host;

        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var result = await connection.Client.CallToolAsync(
            "money_totals",
            new Dictionary<string, object?> { ["period"] = "this_month", ["fromDate"] = "2026-08-01" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        text.Should().Contain("not both");
    }
}
