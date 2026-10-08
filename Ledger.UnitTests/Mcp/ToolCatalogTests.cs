using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.Service.Mcp;
using ModelContextProtocol.Server;

namespace Ledger.UnitTests.Mcp;

/// <summary>
/// Verifies the tool surface by reflection: exactly four read-only tools with snake_case names, and descriptions and server
/// instructions that carry no planning reference, no account number and no instruction about how to treat bank text.
/// </summary>
[Trait("Category", "McpTools")]
public partial class ToolCatalogTests
{
    private static readonly string[] ExpectedTools = ["ledger_overview", "money_totals", "search_transactions", "find_counterparties"];

    [GeneratedRegex(@"\b(SEC|OPS|API|DASH|INGEST|CAT|PLAN|ADV|WEB|REF)-[0-9]{2}\b|\bD-[0-9]{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\b[Pp]hase[-_ ]?[0-9]+\b|\b(PROJECT|REQUIREMENTS|ROADMAP|STATE|RESEARCH|CONTEXT|PLAN|SPEC)\.md\b|\.plannin[g]/")]
    private static partial Regex PlanningPattern();

    [GeneratedRegex(@"\b[A-Za-z]{2}[0-9]{2}[A-Za-z0-9]{10,30}\b")]
    private static partial Regex IbanShapePattern();

    private static IReadOnlyList<(MethodInfo Method, McpServerToolAttribute Tool)> Tools()
    {
        return typeof(LedgerTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(pair => pair.Tool is not null)
            .Select(pair => (pair.Method, pair.Tool!))
            .ToList();
    }

    private static IEnumerable<string> AllDescriptions()
    {
        foreach (var (method, tool) in Tools())
        {
            yield return method.GetCustomAttribute<DescriptionAttribute>()!.Description;
            yield return tool.Title ?? string.Empty;

            foreach (var parameter in method.GetParameters())
            {
                if (parameter.GetCustomAttribute<DescriptionAttribute>() is { } description)
                {
                    yield return description.Description;
                }
            }
        }

        yield return ServerInstructions.Text;
    }

    [Fact]
    public void The_server_offers_exactly_the_four_tools_with_snake_case_names()
    {
        var names = Tools().Select(pair => pair.Tool.Name).ToList();

        names.Should().BeEquivalentTo(ExpectedTools);
        names.Should().OnlyContain(name => Regex.IsMatch(name!, "^[a-z]+(_[a-z]+)*$"));
    }

    [Fact]
    public void Every_tool_is_marked_read_only_non_destructive_idempotent_and_closed_world()
    {
        foreach (var (_, tool) in Tools())
        {
            tool.ReadOnly.Should().BeTrue(tool.Name);
            tool.Destructive.Should().BeFalse(tool.Name);
            tool.Idempotent.Should().BeTrue(tool.Name);
            tool.OpenWorld.Should().BeFalse(tool.Name);
        }
    }

    [Fact]
    public void Every_tool_has_a_description_and_a_title()
    {
        foreach (var (method, tool) in Tools())
        {
            method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().NotBeNullOrWhiteSpace(tool.Name);
            tool.Title.Should().NotBeNullOrWhiteSpace(tool.Name);
        }
    }

    [Fact]
    public void No_description_title_or_instruction_carries_a_planning_reference()
    {
        foreach (var text in AllDescriptions())
        {
            KeyPattern().IsMatch(text).Should().BeFalse(text);
            PlanningPattern().IsMatch(text).Should().BeFalse(text);
        }
    }

    [Fact]
    public void No_description_title_or_instruction_carries_an_account_number()
    {
        foreach (var text in AllDescriptions())
        {
            IbanShapePattern().IsMatch(text).Should().BeFalse(text);
        }
    }

    [Fact]
    public void No_description_or_instruction_labels_or_sanitises_bank_text()
    {
        foreach (var text in AllDescriptions())
        {
            text.Should().NotContainEquivalentOf("untrusted")
                .And.NotContainEquivalentOf("sanitis")
                .And.NotContainEquivalentOf("sanitiz")
                .And.NotContainEquivalentOf("injection");
        }
    }

    [Fact]
    public void The_instructions_route_every_total_to_money_totals_and_name_the_other_tools()
    {
        ServerInstructions.Text.Should()
            .Contain("ledger_overview")
            .And.Contain("money_totals")
            .And.Contain("search_transactions")
            .And.Contain("find_counterparties")
            .And.Contain("Europe/Amsterdam")
            .And.Contain("not by category")
            .And.Contain("never add");
    }

    [Fact]
    public void The_totals_description_points_to_the_counterparty_lookup()
    {
        var totals = Tools().Single(pair => pair.Tool.Name == "money_totals");

        totals.Method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("find_counterparties");
    }

    [Fact]
    public void The_search_description_routes_totals_elsewhere()
    {
        var search = Tools().Single(pair => pair.Tool.Name == "search_transactions");

        search.Method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("money_totals");
    }
}
