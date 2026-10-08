using FluentAssertions;
using Ledger.Domain.Queries;
using Ledger.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Verifies that an account number written inside a description or a counterparty name never reaches a client in full, whatever
/// the spelling, and that the rest of the text is kept.
/// </summary>
[Collection("Database")]
public class FreeTextMaskingTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private async Task<McpTestHost> StartAsync()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await LedgerQuerySeed.SeedFreeTextScenarioAsync(connectionString);

        return await McpTestHost.StartAsync(
            connectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now)));
    }

    private static async Task<string> CallAsync(McpConnection connection, string tool, Dictionary<string, object?> arguments)
    {
        var result = await connection.Client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text)).Replace("\\u2022", "\u2022", StringComparison.Ordinal);
    }

    private static void AssertNoAccountNumber(string text)
    {
        var squeezed = string.Concat(text.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

        squeezed.Should().NotContain("XX00SYNT");

        foreach (var iban in LedgerQuerySeed.FreeTextIbans)
        {
            squeezed.Should().NotContain(IbanText.Normalize(iban)![4..]);
        }
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task No_tool_result_over_the_client_carries_an_account_number_written_in_a_description_or_a_counterparty_name()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var period = new Dictionary<string, object?> { ["fromDate"] = "2026-08-01", ["toDate"] = "2026-08-31" };
        var grouped = new Dictionary<string, object?>(period) { ["groupBy"] = "counterparty" };

        var search = await CallAsync(connection, "search_transactions", period);
        var totals = await CallAsync(connection, "money_totals", grouped);
        var lookup = await CallAsync(connection, "find_counterparties", new Dictionary<string, object?> { ["text"] = "Example" });
        var overview = await CallAsync(connection, "ledger_overview", []);

        foreach (var text in new[] { search, totals, lookup, overview })
        {
            AssertNoAccountNumber(text);
        }

        search.Should().Contain("XX••••0007").And.Contain("Rent August IBAN: XX••••0007 thanks");
        search.Should().Contain("Rent to XX••••0008, reference 4711");
        search.Should().Contain("XX••••0009");
        search.Should().Contain("Example Payee XX••••0010");
        totals.Should().Contain("Example Payee XX••••0010");
        lookup.Should().Contain("Example Payee XX••••0010");
    }

    [Fact]
    [Trait("Category", "McpTools")]
    public async Task Masking_a_counterparty_name_keeps_its_reference_usable_as_a_filter()
    {
        await using var host = await StartAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var reference = CounterpartyRef.For($"Example Payee {LedgerQuerySeed.FreeTextIbans[3]}");

        var text = await CallAsync(
            connection,
            "money_totals",
            new Dictionary<string, object?>
            {
                ["fromDate"] = "2026-08-01",
                ["toDate"] = "2026-08-31",
                ["counterpartyRef"] = new[] { reference }
            });

        text.Should().Contain("13.00");
        AssertNoAccountNumber(text);
    }
}
