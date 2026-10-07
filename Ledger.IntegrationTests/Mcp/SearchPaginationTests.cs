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
/// Verifies transaction search on a real database: a synthetic year paged under a hard cap with an explicit truncation notice,
/// stable keyset order, cursors bound to their search, and the shape of a row.
/// </summary>
[Collection("Database")]
public class SearchPaginationTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private static SearchRequest Year(
        int? limit = null,
        string? cursor = null,
        IReadOnlyList<string>? counterparty = null,
        decimal? minAmount = null,
        decimal? maxAmount = null)
    {
        return new SearchRequest(
            null, "2025-01-01", "2025-12-31", counterparty, null, null, null, null, minAmount, maxAmount, null, limit, cursor);
    }

    private static SearchRequest August(string? status = null, decimal? minAmount = null, decimal? maxAmount = null)
    {
        return new SearchRequest(null, "2026-08-01", "2026-08-31", null, null, null, null, null, minAmount, maxAmount, status, null, null);
    }

    private async Task<(McpTestHost Host, IReadOnlyList<LedgerQuerySeed.YearRow> Expected)> StartYearAsync()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        var expected = await LedgerQuerySeed.SeedYearAsync(connectionString);
        var host = await McpTestHost.StartAsync(
            connectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now)));

        return (host, expected);
    }

    private async Task<McpTestHost> StartTotalsScenarioAsync()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await LedgerQuerySeed.SeedTotalsScenarioAsync(connectionString);

        return await McpTestHost.StartAsync(
            connectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now)));
    }

    private static async Task<SearchResult> SearchAsync(McpTestHost host, SearchRequest request)
    {
        using var scope = host.Factory.Services.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<LedgerQueryService>();

        return await queries.SearchAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task A_year_of_rows_is_walked_in_twelve_pages_that_show_each_row_once_in_a_stable_order()
    {
        var (host, expected) = await StartYearAsync();
        await using var running = host;

        var first = await SearchAsync(host, Year(limit: 100));

        first.Rows.Should().HaveCount(100);
        first.Returned.Should().Be(100);
        first.MatchingTotal.Should().Be(LedgerQuerySeed.YearRowCount);
        first.Truncated.Should().BeTrue();
        first.NextCursor.Should().NotBeNullOrEmpty();
        first.Limit.Should().Be(100);
        first.LimitClamped.Should().BeFalse();
        first.Note.Should().Contain("Showing 100 of 1200 matching transactions")
            .And.Contain("narrow the period or filters")
            .And.Contain("next_cursor")
            .And.Contain("use money_totals");

        var serials = first.Rows.Select(row => row.Description!).ToList();
        var pages = 1;
        var last = first;

        while (last.NextCursor is { } cursor)
        {
            last = await SearchAsync(host, Year(limit: 100, cursor: cursor));
            serials.AddRange(last.Rows.Select(row => row.Description!));
            pages++;
            last.MatchingTotal.Should().Be(LedgerQuerySeed.YearRowCount);
        }

        pages.Should().Be(12);
        last.Truncated.Should().BeFalse();
        last.NextCursor.Should().BeNull();
        last.Rows.Should().HaveCount(100);
        serials.Should().OnlyHaveUniqueItems().And.HaveCount(LedgerQuerySeed.YearRowCount);
        serials.Should().Equal(expected.Select(row => row.Serial));
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task The_rows_that_share_a_date_and_a_first_seen_time_keep_one_order_across_page_boundaries()
    {
        var (host, expected) = await StartYearAsync();
        await using var running = host;

        var tiedStart = expected.ToList().FindIndex(row => row.Date == new DateOnly(2025, 6, 15) && row.FirstSeenAt.Hour == 10 && row.FirstSeenAt.Minute == 0 && row.FirstSeenAt.Second == 0);
        var pageSize = 7;
        var walked = new List<string>();
        string? cursor = null;

        do
        {
            var page = await SearchAsync(host, Year(limit: pageSize, cursor: cursor));
            walked.AddRange(page.Rows.Select(row => row.Description!));
            cursor = page.NextCursor;
        }
        while (cursor is not null && walked.Count < tiedStart + LedgerQuerySeed.YearTiedRowCount + 20);

        walked.Take(tiedStart + LedgerQuerySeed.YearTiedRowCount + 20)
            .Should().Equal(expected.Take(tiedStart + LedgerQuerySeed.YearTiedRowCount + 20).Select(row => row.Serial));
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task Without_a_limit_a_page_has_fifty_rows_and_a_limit_is_clamped_and_reported()
    {
        var (host, _) = await StartYearAsync();
        await using var running = host;

        var standard = await SearchAsync(host, Year());
        standard.Rows.Should().HaveCount(50);
        standard.Limit.Should().Be(50);
        standard.LimitClamped.Should().BeFalse();

        var high = await SearchAsync(host, Year(limit: 150));
        high.Rows.Should().HaveCount(100);
        high.Limit.Should().Be(100);
        high.LimitClamped.Should().BeTrue();

        var low = await SearchAsync(host, Year(limit: 0));
        low.Rows.Should().HaveCount(1);
        low.Limit.Should().Be(1);
        low.LimitClamped.Should().BeTrue();
        low.Truncated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task A_search_that_fits_exactly_in_one_page_is_not_truncated_and_one_more_row_makes_a_second_page()
    {
        var (host, _) = await StartYearAsync();
        await using var running = host;

        var exact = await SearchAsync(host, Year(limit: 100, counterparty: ["boundary shop"], maxAmount: 1.5m));

        exact.MatchingTotal.Should().Be(100);
        exact.Returned.Should().Be(100);
        exact.Truncated.Should().BeFalse();
        exact.NextCursor.Should().BeNull();
        exact.Note.Should().NotContain("narrow");

        var over = await SearchAsync(host, Year(limit: 100, counterparty: ["boundary shop"]));

        over.MatchingTotal.Should().Be(101);
        over.Truncated.Should().BeTrue();
        over.NextCursor.Should().NotBeNullOrEmpty();

        var second = await SearchAsync(host, Year(limit: 100, counterparty: ["boundary shop"], cursor: over.NextCursor));

        second.Returned.Should().Be(1);
        second.Truncated.Should().BeFalse();
        second.NextCursor.Should().BeNull();
        second.MatchingTotal.Should().Be(101);
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task Amount_bounds_are_inclusive_and_amounts_are_exact_signed_text()
    {
        var (host, _) = await StartYearAsync();
        await using var running = host;

        var result = await SearchAsync(host, Year(counterparty: ["boundary shop"], minAmount: 2m, maxAmount: 2m));

        result.Rows.Should().ContainSingle();
        result.Rows[0].Amount.Should().Be("-2.00");
        result.Rows[0].Direction.Should().Be("out");
        result.Filters.MinAmount.Should().Be("2.00");
        result.Filters.MaxAmount.Should().Be("2.00");
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task A_cursor_used_with_other_filters_or_a_malformed_cursor_is_refused_with_a_plain_message()
    {
        var (host, _) = await StartYearAsync();
        await using var running = host;

        var first = await SearchAsync(host, Year(limit: 10, counterparty: ["boundary shop"]));

        var other = async () => await SearchAsync(host, Year(limit: 10, counterparty: ["merchant"], cursor: first.NextCursor));
        var malformed = async () => await SearchAsync(host, Year(limit: 10, cursor: "definitely-not-a-cursor"));

        (await other.Should().ThrowAsync<LedgerQueryException>())
            .WithMessage("This cursor belongs to a different search; repeat the search without a cursor.");
        (await malformed.Should().ThrowAsync<LedgerQueryException>())
            .WithMessage("The cursor is not valid; repeat the search without a cursor.");
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task A_result_never_carries_a_sum_or_page_total_of_money()
    {
        var (host, _) = await StartYearAsync();
        await using var running = host;

        var result = await SearchAsync(host, Year(limit: 5));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var names = PropertyNames(document.RootElement).ToList();

        names.Should().NotContain(name => name.Contains("sum", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(name => name.Contains("page_total", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(name => name.Contains("money", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(name => name.Contains("net", StringComparison.OrdinalIgnoreCase));
        document.RootElement.GetProperty("matching_total").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task Rows_show_status_masked_accounts_and_transfers_between_own_accounts_but_never_dropped_rows_or_full_accounts()
    {
        await using var host = await StartTotalsScenarioAsync();

        var result = await SearchAsync(host, August());
        var json = JsonSerializer.Serialize(result);

        json.Should().NotContain("XX00SYNT").And.NotContain("xx00").And.NotContain("synt").And.NotContain(LedgerQuerySeed.JointProviderName);
        json.Should().NotContain("Never settled").And.NotContain("99.00");

        var transfers = result.Rows.Where(row => row.InternalTransfer).ToList();
        transfers.Should().HaveCount(2);
        transfers.Select(row => row.Description).Should().BeEquivalentTo("Move to savings", "From joint");
        transfers.Select(row => row.AccountName).Should().BeEquivalentTo("Joint", "Savings");

        result.Rows.Where(row => !row.InternalTransfer).Should().OnlyContain(row => row.CounterpartyName != "Savings" && row.CounterpartyName != "Joint");
        result.Rows.Single(row => row.CounterpartyName == "Other own").InternalTransfer.Should().BeFalse();

        var market = result.Rows.First(row => row.CounterpartyName == "Example Market" && row.Currency == "EUR");
        market.CounterpartyAccount.Should().Be(IbanText.Mask("XX00SYNT9999999999"));
        market.CounterpartyAccount.Should().EndWith("9999");
        market.CounterpartyRef.Should().Be(CounterpartyRef.For("Example Market"));
        market.Status.Should().Be("booked");
        market.Currency.Should().Be("EUR");
        market.AccountKey.Should().NotBeNullOrEmpty();

        var pending = result.Rows.Where(row => row.Status == "pending").ToList();
        pending.Should().ContainSingle();
        pending[0].Amount.Should().Be("-12.50");
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task A_status_filter_returns_only_that_status_and_amount_bounds_keep_the_boundary_row()
    {
        await using var host = await StartTotalsScenarioAsync();

        var pending = await SearchAsync(host, August(status: "pending"));
        pending.Rows.Should().ContainSingle().Which.Status.Should().Be("pending");
        pending.Filters.Status.Should().Be("pending");

        var booked = await SearchAsync(host, August(status: "booked"));
        booked.Rows.Should().NotBeEmpty().And.OnlyContain(row => row.Status == "booked");

        var bounded = await SearchAsync(host, August(minAmount: 25.5m, maxAmount: 25.5m));
        bounded.Rows.Should().ContainSingle();
        bounded.Rows[0].Amount.Should().Be("-25.50");
        bounded.Rows[0].CounterpartyName.Should().Be("EXAMPLE  market ");
    }

    [Fact]
    [Trait("Category", "Search")]
    public async Task The_search_tool_returns_the_page_as_text_through_the_mcp_client_without_any_full_account_number()
    {
        await using var host = await StartTotalsScenarioAsync();

        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var result = await connection.Client.CallToolAsync(
            "search_transactions",
            new Dictionary<string, object?>
            {
                ["fromDate"] = "2026-08-01",
                ["toDate"] = "2026-08-31",
                ["counterparty"] = new[] { "example market" },
                ["limit"] = 1
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        using var document = JsonDocument.Parse(text);

        text.Should().NotContain("XX00SYNT");
        document.RootElement.GetProperty("returned").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("matching_total").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("next_cursor").GetString().Should().NotBeNullOrEmpty();
        document.RootElement.GetProperty("rows")[0].GetProperty("counterparty_account").GetString().Should().EndWith("9999");
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;

                foreach (var nested in PropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in PropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
