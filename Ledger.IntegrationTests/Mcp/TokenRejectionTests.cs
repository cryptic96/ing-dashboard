using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.OAuth;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Measures, on the real host's metrics endpoint, which access tokens the MCP endpoint counts as rejected and why, that an absent
/// token is never counted, and that approving a connection and reusing a refresh token each move their own counter.
/// </summary>
[Collection("Database")]
public class TokenRejectionTests(DatabaseFixture fixture)
{
    private const string Expired = "ledger_mcp_rejected_tokens_total{reason=\"expired\"}";
    private const string WrongAudience = "ledger_mcp_rejected_tokens_total{reason=\"wrong_audience\"}";
    private const string Invalid = "ledger_mcp_rejected_tokens_total{reason=\"invalid\"}";
    private const string GrantsCreated = "ledger_oauth_grants_created_total";
    private const string RefreshReuse = "ledger_oauth_refresh_token_reuse_total";
    private const string OverviewCalls = "ledger_mcp_tool_calls_total{tool=\"ledger_overview\"}";
    private const string OtherPublicBaseUrl = "https://other.example.com";

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task An_expired_access_token_is_counted_as_expired()
    {
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            new Dictionary<string, string?> { ["OAuth:AccessTokenLifetime"] = "00:00:02" });
        var login = await host.CreateLoginAsync();
        var (_, tokens) = await host.SignInAsync(login);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var before = await ReadAsync(host);

        (await host.ToolsListStatusAsync(tokens.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);

        var after = await ReadAsync(host);
        Delta(before, after, Expired).Should().Be(1);
        Delta(before, after, WrongAudience).Should().Be(0);
        Delta(before, after, Invalid).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_token_issued_for_another_resource_is_counted_as_wrong_audience()
    {
        var connectionString = fixture.ConnectionStringFor("ledger_runtime");
        await using var host = await McpTestHost.StartAsync(connectionString);
        await using var other = await McpTestHost.StartAsync(
            connectionString,
            new Dictionary<string, string?> { ["OAuth:PublicBaseUrl"] = OtherPublicBaseUrl });
        var login = await other.CreateLoginAsync();
        var (_, foreign) = await other.SignInAsync(login);
        var before = await ReadAsync(host);

        (await host.ToolsListStatusAsync(foreign.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);

        var after = await ReadAsync(host);
        Delta(before, after, WrongAudience).Should().Be(1);
        Delta(before, after, Expired).Should().Be(0);
        Delta(before, after, Invalid).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_random_bearer_value_is_counted_as_invalid()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var before = await ReadAsync(host);

        (await host.ToolsListStatusAsync(Guid.NewGuid().ToString("N"))).Should().Be(HttpStatusCode.Unauthorized);

        var after = await ReadAsync(host);
        Delta(before, after, Invalid).Should().Be(1);
        Delta(before, after, Expired).Should().Be(0);
        Delta(before, after, WrongAudience).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_revoked_access_token_is_counted_as_invalid()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        var (_, tokens) = await host.SignInAsync(login);

        using (var scope = host.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<GrantRevocationService>().RevokeAllAsync(TestContext.Current.CancellationToken);
        }

        var before = await ReadAsync(host);

        (await host.ToolsListStatusAsync(tokens.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);

        var after = await ReadAsync(host);
        Delta(before, after, Invalid).Should().Be(1);
        Delta(before, after, Expired).Should().Be(0);
        Delta(before, after, WrongAudience).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_request_without_a_token_or_with_an_empty_one_is_challenged_and_not_counted()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var before = await ReadAsync(host);

        using var none = await host.PostToolsListAsync(null);
        using var empty = await PostWithAuthorizationAsync(host, "Bearer ");

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        none.Headers.WwwAuthenticate.Should().Contain(value => value.Scheme == "Bearer");
        empty.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        empty.Headers.WwwAuthenticate.Should().Contain(value => value.Scheme == "Bearer");

        var after = await ReadAsync(host);
        Delta(before, after, Invalid).Should().Be(0);
        Delta(before, after, Expired).Should().Be(0);
        Delta(before, after, WrongAudience).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Every_series_is_present_from_startup_and_labels_hold_only_fixed_words()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));

        var text = await MetricsTextAsync(host);

        foreach (var series in new[] { Expired, WrongAudience, Invalid, GrantsCreated, RefreshReuse, OverviewCalls })
        {
            text.Should().Contain(series + " ");
        }

        text.Split('\n')
            .Where(line => line.StartsWith("ledger_mcp_rejected_tokens_total{", StringComparison.Ordinal))
            .Should().OnlyContain(line => line.Contains("{reason=\"", StringComparison.Ordinal) && line.Count(c => c == '=') == 1);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Approving_a_consent_counts_one_grant_and_a_tool_call_counts_that_tool()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await LedgerQuerySeed.SeedStandardLedgerAsync(connectionString);
        await using var host = await McpTestHost.StartAsync(connectionString);
        var login = await host.CreateLoginAsync();
        var before = await ReadAsync(host);

        await using var connection = await host.ConnectAsync(login);

        var afterConsent = await ReadAsync(host);
        Delta(before, afterConsent, GrantsCreated).Should().Be(1);
        Delta(before, afterConsent, RefreshReuse).Should().Be(0);

        await connection.Client.CallToolAsync("ledger_overview", cancellationToken: TestContext.Current.CancellationToken);

        var afterCall = await ReadAsync(host);
        Delta(afterConsent, afterCall, OverviewCalls).Should().Be(1);
        Delta(afterConsent, afterCall, GrantsCreated).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Reusing_a_rotated_refresh_token_is_counted_and_logged_without_any_token_text()
    {
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            new Dictionary<string, string?> { ["OAuth:RefreshTokenReuseLeeway"] = "00:00:00" });
        var login = await host.CreateLoginAsync();
        var (discovery, first) = await host.SignInAsync(login);

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var second = await driver.RefreshAsync(discovery, ClientRegistrations.CodeClientId, first.RefreshToken!);
        second.Succeeded.Should().BeTrue();
        var before = await ReadAsync(host);

        var reused = await driver.RefreshAsync(discovery, ClientRegistrations.CodeClientId, first.RefreshToken!);

        reused.Error.Should().Be("invalid_grant");
        var after = await ReadAsync(host);
        Delta(before, after, RefreshReuse).Should().Be(1);

        var warnings = host.Factory.CapturedLogMessages
            .Where(message => message.Contains("A refresh token was reused", StringComparison.Ordinal))
            .ToList();
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("every token of its grant was revoked");

        foreach (var secret in new[] { first.RefreshToken!, first.AccessToken!, second.RefreshToken!, second.AccessToken! })
        {
            host.Factory.CapturedLogMessages.Should().NotContain(message => message.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_refresh_inside_the_leeway_is_not_counted_as_a_reuse()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        var (discovery, first) = await host.SignInAsync(login);
        var before = await ReadAsync(host);

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        (await driver.RefreshAsync(discovery, ClientRegistrations.CodeClientId, first.RefreshToken!)).Succeeded.Should().BeTrue();
        (await driver.RefreshAsync(discovery, ClientRegistrations.CodeClientId, first.RefreshToken!)).Succeeded.Should().BeTrue();

        var after = await ReadAsync(host);
        Delta(before, after, RefreshReuse).Should().Be(0);
    }

    private static async Task<HttpResponseMessage> PostWithAuthorizationAsync(McpTestHost host, string authorization)
    {
        using var browser = host.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return await browser.SendAsync(request, HttpCompletionOption.ResponseContentRead, TestContext.Current.CancellationToken);
    }

    private static async Task<string> MetricsTextAsync(McpTestHost host)
    {
        using var ops = host.Factory.CreateOpsClient();

        return await ops.GetStringAsync("/metrics", TestContext.Current.CancellationToken);
    }

    private static async Task<Dictionary<string, double>> ReadAsync(McpTestHost host)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var line in (await MetricsTextAsync(host)).Split('\n'))
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

    private static double Delta(Dictionary<string, double> before, Dictionary<string, double> after, string series)
    {
        before.Should().ContainKey(series);
        after.Should().ContainKey(series);

        return after[series] - before[series];
    }
}
