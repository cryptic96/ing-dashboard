using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Ledger.Service.Mcp;
using Ledger.Service.OAuth;
using ModelContextProtocol.Server;
using Prometheus;

namespace Ledger.UnitTests.Mcp;

/// <summary>
/// Verifies the MCP and token counters: every series exists from startup, labels stay inside the fixed words, and OpenIddict's
/// error identifiers map to the right reason.
/// </summary>
[Trait("Category", "Metrics")]
public class McpMetricsTests
{
    private const string ExpiredSeries = "ledger_mcp_rejected_tokens_total{reason=\"expired\"}";
    private const string WrongAudienceSeries = "ledger_mcp_rejected_tokens_total{reason=\"wrong_audience\"}";
    private const string InvalidSeries = "ledger_mcp_rejected_tokens_total{reason=\"invalid\"}";
    private const string GrantsSeries = "ledger_oauth_grants_created_total";
    private const string ReuseSeries = "ledger_oauth_refresh_token_reuse_total";
    private const string DocumentationBase = "https://documentation.openiddict.com/errors/";

    [Fact]
    public async Task Every_series_exists_after_the_counters_are_initialised()
    {
        McpMetrics.InitialiseCounters();

        var values = await ScrapeAsync();

        foreach (var tool in DeclaredToolNames())
        {
            values.Should().ContainKey($"ledger_mcp_tool_calls_total{{tool=\"{tool}\"}}");
        }

        values.Should().ContainKeys(ExpiredSeries, WrongAudienceSeries, InvalidSeries, GrantsSeries, ReuseSeries);
    }

    [Fact]
    public void The_declared_tools_are_found_by_reflection_and_none_is_empty()
    {
        DeclaredToolNames().Should().NotBeEmpty().And.OnlyContain(name => name.Length > 0);
    }

    [Fact]
    public async Task Each_rejection_reason_moves_only_its_own_series()
    {
        McpMetrics.InitialiseCounters();
        var before = await ScrapeAsync();

        McpMetrics.TokenRejected(McpMetrics.ReasonExpired);
        McpMetrics.TokenRejected(McpMetrics.ReasonWrongAudience);
        McpMetrics.TokenRejected(McpMetrics.ReasonWrongAudience);
        McpMetrics.TokenRejected(McpMetrics.ReasonInvalid);

        var after = await ScrapeAsync();
        (after[ExpiredSeries] - before[ExpiredSeries]).Should().Be(1);
        (after[WrongAudienceSeries] - before[WrongAudienceSeries]).Should().Be(2);
        (after[InvalidSeries] - before[InvalidSeries]).Should().Be(1);
    }

    [Fact]
    public async Task A_reason_outside_the_fixed_words_is_counted_as_invalid_so_no_new_series_appears()
    {
        McpMetrics.InitialiseCounters();
        var before = await ScrapeAsync();

        McpMetrics.TokenRejected("a-token-value-or-anything-else");

        var after = await ScrapeAsync();
        (after[InvalidSeries] - before[InvalidSeries]).Should().Be(1);
        after.Keys.Where(key => key.StartsWith("ledger_mcp_rejected_tokens_total{", StringComparison.Ordinal))
            .Should().BeEquivalentTo(ExpiredSeries, WrongAudienceSeries, InvalidSeries);
    }

    [Fact]
    public async Task A_grant_and_a_refresh_reuse_move_their_own_counters()
    {
        McpMetrics.InitialiseCounters();
        var before = await ScrapeAsync();

        McpMetrics.GrantCreated();
        McpMetrics.RefreshTokenReused();
        McpMetrics.RefreshTokenReused();

        var after = await ScrapeAsync();
        (after[GrantsSeries] - before[GrantsSeries]).Should().Be(1);
        (after[ReuseSeries] - before[ReuseSeries]).Should().Be(2);
    }

    [Fact]
    public void The_rejection_reasons_are_exactly_the_three_fixed_words()
    {
        McpMetrics.RejectionReasons.Should().BeEquivalentTo("expired", "wrong_audience", "invalid");
    }

    [Theory]
    [InlineData("ID2093", McpMetrics.ReasonWrongAudience)]
    [InlineData("ID2094", McpMetrics.ReasonWrongAudience)]
    [InlineData("ID2019", McpMetrics.ReasonExpired)]
    [InlineData("ID2004", McpMetrics.ReasonInvalid)]
    [InlineData("ID2000", McpMetrics.ReasonInvalid)]
    [InlineData("ID2090", McpMetrics.ReasonInvalid)]
    public void Classify_maps_the_openiddict_identifiers_to_a_reason(string identifier, string expected)
    {
        TokenRejectionReasons.Classify("invalid_token", "text", DocumentationBase + identifier).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("invalid_token", null, null)]
    [InlineData("invalid_token", "The specified token is invalid.", "")]
    [InlineData("invalid_token", "text", "https://example.com/ID2019x")]
    public void Classify_treats_missing_or_unrecognised_information_as_invalid(string? error, string? description, string? uri)
    {
        TokenRejectionReasons.Classify(error, description, uri).Should().Be(McpMetrics.ReasonInvalid);
    }

    [Fact]
    public void An_expired_reason_stays_expired_only_when_the_stored_entry_has_passed_its_expiry()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        TokenRejectionReasons.ConfirmExpiry(McpMetrics.ReasonExpired, now.AddSeconds(-1), now).Should().Be(McpMetrics.ReasonExpired);
        TokenRejectionReasons.ConfirmExpiry(McpMetrics.ReasonExpired, now, now).Should().Be(McpMetrics.ReasonExpired);
        TokenRejectionReasons.ConfirmExpiry(McpMetrics.ReasonExpired, now.AddSeconds(1), now).Should().Be(McpMetrics.ReasonInvalid);
        TokenRejectionReasons.ConfirmExpiry(McpMetrics.ReasonExpired, null, now).Should().Be(McpMetrics.ReasonInvalid);
        TokenRejectionReasons.ConfirmExpiry(McpMetrics.ReasonWrongAudience, null, now).Should().Be(McpMetrics.ReasonWrongAudience);
        TokenRejectionReasons.ConfirmExpiry(McpMetrics.ReasonInvalid, now.AddDays(-1), now).Should().Be(McpMetrics.ReasonInvalid);
    }

    [Theory]
    [InlineData("invalid_grant", "ID2012", true)]
    [InlineData("invalid_grant", "ID2001", false)]
    [InlineData("invalid_grant", "ID2019", false)]
    [InlineData("invalid_request", "ID2012", false)]
    [InlineData(null, null, false)]
    public void A_refresh_token_reuse_is_recognised_only_by_its_own_identifier_on_an_invalid_grant(string? error, string? identifier, bool expected)
    {
        var uri = identifier is null ? null : DocumentationBase + identifier;

        TokenRejectionReasons.IsRefreshTokenReuse(error, "text", uri).Should().Be(expected);
    }

    private static IEnumerable<string> DeclaredToolNames()
    {
        return typeof(LedgerTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute?.Name is not null)
            .Select(attribute => attribute!.Name!);
    }

    private static async Task<Dictionary<string, double>> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, TestContext.Current.CancellationToken);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var line in System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n'))
        {
            var separator = line.LastIndexOf(' ');

            if (line.StartsWith("ledger_", StringComparison.Ordinal)
                && separator > 0
                && double.TryParse(line[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values[line[..separator]] = value;
            }
        }

        return values;
    }
}
