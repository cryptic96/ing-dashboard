using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>Verifies that dates at the end of the calendar and an unusable time zone are refused with a plain message over the client.</summary>
[Collection("Database")]
public class QueryEdgeInputTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private async Task<McpTestHost> StartAsync(string? timeZone = null)
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await LedgerQuerySeed.SeedTotalsScenarioAsync(connectionString);

        var configuration = new Dictionary<string, string?> { ["Ingestion:SchedulerEnabled"] = "false" };

        if (timeZone is not null)
        {
            configuration["Ingestion:TimeZone"] = timeZone;
        }

        return await McpTestHost.StartAsync(
            connectionString,
            configuration,
            services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now)));
    }

    private static async Task<(CallToolResult Result, string Text)> CallAsync(McpConnection connection, string tool, Dictionary<string, object?> arguments)
    {
        var result = await connection.Client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        return (result, text);
    }

    [Theory]
    [Trait("Category", "McpTools")]
    [InlineData("day")]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("none")]
    public async Task A_period_ending_on_the_last_day_of_the_calendar_is_refused_with_a_plain_message(string groupBy)
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (result, message) = await CallAsync(
            connection,
            "money_totals",
            new Dictionary<string, object?> { ["fromDate"] = "9999-12-01", ["toDate"] = "9999-12-31", ["groupBy"] = groupBy });

        result.IsError.Should().BeTrue();
        message.Should().Contain("9000-12-31");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task A_search_and_a_counterparty_lookup_ending_on_the_last_day_of_the_calendar_are_refused_with_a_plain_message()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (searchResult, searchMessage) = await CallAsync(
            connection,
            "search_transactions",
            new Dictionary<string, object?> { ["fromDate"] = "9999-12-30", ["toDate"] = "9999-12-31" });
        var (lookupResult, lookupMessage) = await CallAsync(
            connection,
            "find_counterparties",
            new Dictionary<string, object?> { ["text"] = "Example", ["fromDate"] = "9999-12-30", ["toDate"] = "9999-12-31" });

        searchResult.IsError.Should().BeTrue();
        searchMessage.Should().Contain("9000-12-31");
        lookupResult.IsError.Should().BeTrue();
        lookupMessage.Should().Contain("9000-12-31");
    }

    [Theory]
    [Trait("Category", "McpTools")]
    [InlineData("Not/AZone")]
    [InlineData("   ")]
    public async Task A_time_zone_that_does_not_exist_is_refused_by_every_tool_with_a_plain_message(string timeZone)
    {
        await using var host = await StartAsync(timeZone);
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var period = new Dictionary<string, object?> { ["period"] = "this_month" };
        var lookup = new Dictionary<string, object?> { ["text"] = "Example" };

        foreach (var (tool, arguments) in new[]
        {
            ("ledger_overview", new Dictionary<string, object?>()),
            ("money_totals", period),
            ("search_transactions", period),
            ("find_counterparties", lookup)
        })
        {
            var (result, message) = await CallAsync(connection, tool, arguments);

            result.IsError.Should().BeTrue(tool);
            message.Should().Contain("IANA", tool);
        }
    }
}
