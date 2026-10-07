using System.Text.Json;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Verifies the tool surface a connected client sees on a real database: exactly four read-only tools, the counterparty lookup,
/// the instructions it receives at connection, and that tool arguments and full account numbers never leave the server.
/// </summary>
[Collection("Database")]
public class ToolSurfaceTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private async Task<McpTestHost> StartAsync()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await LedgerQuerySeed.SeedTotalsScenarioAsync(connectionString);

        return await McpTestHost.StartAsync(
            connectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now)));
    }

    private static async Task<(CallToolResult Result, string Text)> CallAsync(McpConnection connection, string tool, Dictionary<string, object?> arguments)
    {
        var result = await connection.Client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        return (result, text);
    }

    private static Dictionary<string, object?> Lookup(string text, int? limit = null)
    {
        var arguments = new Dictionary<string, object?> { ["text"] = text };

        if (limit is not null)
        {
            arguments["limit"] = limit;
        }

        return arguments;
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task The_server_lists_exactly_four_tools_each_marked_read_only_non_destructive_idempotent_and_closed_world()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var tools = await connection.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).Should().BeEquivalentTo("ledger_overview", "money_totals", "search_transactions", "find_counterparties");

        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            annotations.Should().NotBeNull(tool.Name);
            annotations!.ReadOnlyHint.Should().BeTrue(tool.Name);
            annotations.DestructiveHint.Should().BeFalse(tool.Name);
            annotations.IdempotentHint.Should().BeTrue(tool.Name);
            annotations.OpenWorldHint.Should().BeFalse(tool.Name);
        }
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task The_money_totals_tool_accepts_exactly_the_documented_parameters()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var tools = await connection.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var totals = tools.Single(tool => tool.Name == "money_totals");

        var properties = totals.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

        properties.Should().BeEquivalentTo(
            "period", "fromDate", "toDate", "counterparty", "counterpartyRef", "description", "accounts", "direction", "minAmount", "maxAmount", "groupBy");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task The_instructions_given_at_connection_name_the_first_tool_the_calendar_and_the_grouping()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var instructions = connection.Client.ServerInstructions;

        instructions.Should().Contain("ledger_overview")
            .And.Contain("money_totals")
            .And.Contain("Europe/Amsterdam")
            .And.Contain("not by category")
            .And.Contain("find_counterparties");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task Finding_a_counterparty_merges_its_spellings_under_one_reference_with_totals_per_currency_and_masked_accounts()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (result, text) = await CallAsync(connection, "find_counterparties", Lookup("MARKET"));

        result.IsError.Should().NotBe(true);
        text.Should().NotContain("XX00SYNT");

        using var document = JsonDocument.Parse(text);
        var counterparties = document.RootElement.GetProperty("counterparties");
        counterparties.GetArrayLength().Should().Be(1);
        document.RootElement.GetProperty("period").GetString().Should().Be("all history");
        document.RootElement.GetProperty("matching_total").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("note").GetString().Should().Contain("counterparty names, not categories");

        var market = counterparties[0];
        market.GetProperty("name").GetString().Should().Be("Example Market");
        market.GetProperty("counterparty_ref").GetString().Should().StartWith("cp_");
        market.GetProperty("spellings").EnumerateArray().Select(spelling => spelling.GetString())
            .Should().BeEquivalentTo("Example Market", "EXAMPLE  market ");
        market.GetProperty("transaction_count").GetInt32().Should().Be(3);
        market.GetProperty("first_date").GetString().Should().Be("2026-08-03");
        market.GetProperty("last_date").GetString().Should().Be("2026-08-12");
        market.GetProperty("accounts").EnumerateArray().Select(account => account.GetString()).Should().Equal("XX••••9999");

        var euro = market.GetProperty("currencies").EnumerateArray().Single(currency => currency.GetProperty("currency").GetString() == "EUR");
        euro.GetProperty("count").GetInt32().Should().Be(2);
        euro.GetProperty("money_out").GetString().Should().Be("65.50");
        euro.GetProperty("money_in").GetString().Should().Be("0.00");

        var dollar = market.GetProperty("currencies").EnumerateArray().Single(currency => currency.GetProperty("currency").GetString() == "USD");
        dollar.GetProperty("count").GetInt32().Should().Be(1);
        dollar.GetProperty("money_out").GetString().Should().Be("10.00");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task The_counterparty_lookup_leaves_out_transfers_between_the_synced_accounts_and_keeps_other_own_accounts()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (_, transferText) = await CallAsync(connection, "find_counterparties", Lookup("savings"));
        using var transfers = JsonDocument.Parse(transferText);
        transfers.RootElement.GetProperty("counterparties").GetArrayLength().Should().Be(0);
        transfers.RootElement.GetProperty("matching_total").GetInt32().Should().Be(0);

        var (_, otherText) = await CallAsync(connection, "find_counterparties", Lookup("other own"));
        using var other = JsonDocument.Parse(otherText);
        other.RootElement.GetProperty("counterparties").GetArrayLength().Should().Be(1);
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task The_counterparty_lookup_is_capped_and_says_how_many_names_matched()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (_, text) = await CallAsync(connection, "find_counterparties", Lookup("example", limit: 2));
        using var document = JsonDocument.Parse(text);

        document.RootElement.GetProperty("returned").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("matching_total").GetInt32().Should().Be(5 + LedgerQuerySeed.ManyCounterpartiesCount);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("limit").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("limit_clamped").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("note").GetString().Should().Contain("Showing 2 of 35");

        var (_, clampedText) = await CallAsync(connection, "find_counterparties", Lookup("example", limit: 500));
        using var clamped = JsonDocument.Parse(clampedText);
        clamped.RootElement.GetProperty("limit").GetInt32().Should().Be(100);
        clamped.RootElement.GetProperty("limit_clamped").GetBoolean().Should().BeTrue();
        clamped.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "McpTools")]
    [InlineData("a")]
    [InlineData(" b ")]
    [InlineData("")]
    public async Task A_lookup_text_shorter_than_two_characters_is_refused_with_a_plain_message(string text)
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (result, message) = await CallAsync(connection, "find_counterparties", Lookup(text));

        result.IsError.Should().BeTrue();
        message.Should().Contain("between 2 and 100");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task A_reference_from_the_lookup_totals_exactly_that_counterparty_in_the_totals_tool()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (_, lookupText) = await CallAsync(connection, "find_counterparties", Lookup("market"));
        using var lookup = JsonDocument.Parse(lookupText);
        var reference = lookup.RootElement.GetProperty("counterparties")[0].GetProperty("counterparty_ref").GetString()!;

        var (_, totalsText) = await CallAsync(
            connection,
            "money_totals",
            new Dictionary<string, object?>
            {
                ["fromDate"] = "2026-08-01",
                ["toDate"] = "2026-08-31",
                ["counterpartyRef"] = new[] { reference }
            });

        using var totals = JsonDocument.Parse(totalsText);
        var euro = totals.RootElement.GetProperty("currencies").EnumerateArray()
            .Single(currency => currency.GetProperty("currency").GetString() == "EUR");

        euro.GetProperty("money_out").GetString().Should().Be("65.50");
        euro.GetProperty("transaction_count").GetInt32().Should().Be(2);
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task A_lookup_can_be_narrowed_to_a_period()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var (_, text) = await CallAsync(
            connection,
            "find_counterparties",
            new Dictionary<string, object?> { ["text"] = "market", ["fromDate"] = "2026-08-04", ["toDate"] = "2026-08-31" });
        using var document = JsonDocument.Parse(text);
        var market = document.RootElement.GetProperty("counterparties")[0];

        document.RootElement.GetProperty("period").GetString().Should().Contain("2026-08-04 to 2026-08-31");
        market.GetProperty("transaction_count").GetInt32().Should().Be(2);
        market.GetProperty("first_date").GetString().Should().Be("2026-08-10");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task Filter_arguments_never_reach_the_logs_and_no_result_carries_a_full_account_number()
    {
        const string Sentinel = "zz-sentinel-filter-4711";

        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var results = new List<string>();

        results.Add((await CallAsync(
            connection,
            "search_transactions",
            new Dictionary<string, object?> { ["fromDate"] = "2026-08-01", ["toDate"] = "2026-08-31", ["counterparty"] = new[] { Sentinel }, ["description"] = new[] { Sentinel } })).Text);
        results.Add((await CallAsync(
            connection,
            "money_totals",
            new Dictionary<string, object?> { ["fromDate"] = "2026-08-01", ["toDate"] = "2026-08-31", ["counterparty"] = new[] { Sentinel }, ["description"] = new[] { Sentinel } })).Text);
        results.Add((await CallAsync(connection, "find_counterparties", Lookup(Sentinel))).Text);
        results.Add((await CallAsync(connection, "ledger_overview", [])).Text);
        results.Add((await CallAsync(
            connection,
            "search_transactions",
            new Dictionary<string, object?> { ["fromDate"] = "2026-08-01", ["toDate"] = "2026-08-31" })).Text);
        results.Add((await CallAsync(connection, "find_counterparties", Lookup("example"))).Text);

        host.Factory.CapturedLogMessages.Should().NotContain(message => message.Contains(Sentinel, StringComparison.OrdinalIgnoreCase));

        foreach (var text in results)
        {
            text.Should().NotContain(LedgerQuerySeed.JointIban)
                .And.NotContain(LedgerQuerySeed.SavingsIban)
                .And.NotContain(LedgerQuerySeed.OtherOwnIban)
                .And.NotContain("XX00SYNT9999999999")
                .And.NotContain("XX00SYNT")
                .And.NotContainEquivalentOf("xx00 synt")
                .And.NotContain(LedgerQuerySeed.JointProviderName);
        }
    }
}
