using System.Net;
using System.Text.Json;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.OAuth;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using OpenIddict.Validation;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Proves a spec-following MCP client can discover the server, sign in with consent and PKCE, receive audience-bound tokens and
/// read the ledger overview, and that tokens behave as intended over time.
/// </summary>
[Collection("Database")]
public class OAuthFlowTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Discovery_chain_names_one_resource_one_authorization_server_and_no_registration_endpoint()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        using var browser = host.CreateBrowser();

        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();

        discovery.Challenge.Should().StartWith("Bearer");
        discovery.ResourceMetadataUrl.Should().Be("https://mcp.example.com/.well-known/oauth-protected-resource/mcp");

        discovery.ResourceMetadata.GetProperty("resource").GetString().Should().Be("https://mcp.example.com/mcp");
        var servers = discovery.ResourceMetadata.GetProperty("authorization_servers").EnumerateArray().ToList();
        servers.Should().HaveCount(1);
        discovery.ResourceMetadata.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString())
            .Should().Contain("ledger.read");

        var metadata = discovery.AuthorizationServerMetadata;
        metadata.GetProperty("issuer").GetString().Should().Be(servers[0].GetString(), "the issuer must match the resource metadata character for character");
        metadata.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(method => method.GetString())
            .Should().Contain("S256");
        metadata.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString())
            .Should().Contain(["ledger.read", "offline_access"]);
        metadata.TryGetProperty("registration_endpoint", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_client_signs_in_with_consent_and_pkce_and_reads_the_overview_without_any_account_number()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        var accounts = await LedgerQuerySeed.SeedStandardLedgerAsync(connectionString);

        await using var host = await McpTestHost.StartAsync(connectionString);
        var login = await host.CreateLoginAsync();

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var outcome = await driver.AuthorizeAsync(
            discovery,
            ClientRegistrations.CodeClientId,
            login,
            OAuthTestDriver.LoopbackRedirectUri);

        outcome.ConsentHtml.Should().Contain("Claude Code").And.Contain("localhost:53682");

        var tokens = await driver.ExchangeAsync(discovery, ClientRegistrations.CodeClientId, outcome);
        tokens.Succeeded.Should().BeTrue();
        tokens.RefreshToken.Should().NotBeNullOrEmpty();

        await using var connection = await host.OpenAsync(tokens.AccessToken!, tokens.RefreshToken!);
        var tools = await connection.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        tools.Select(tool => tool.Name).Should().Equal("ledger_overview");

        var result = await connection.Client.CallToolAsync(
            "ledger_overview",
            cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        foreach (var account in accounts)
        {
            text.Should().Contain(account.DisplayName).And.Contain(account.AccountKey);
            text.Should().NotContain(account.Iban);
        }

        text.Should().NotContain(LedgerQuerySeed.JointProviderName).And.NotContain("Not synced");

        using var overview = JsonDocument.Parse(text);
        overview.RootElement.GetProperty("time_zone").GetString().Should().Be("Europe/Amsterdam");
        overview.RootElement.TryGetProperty("today", out _).Should().BeTrue();

        var joint = overview.RootElement.GetProperty("accounts").EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == "Joint");
        joint.GetProperty("currency").GetString().Should().Be("EUR");
        joint.GetProperty("pending_count").GetInt32().Should().Be(1);
        joint.GetProperty("history_from").GetString().Should().Be("2026-02-03");
        joint.GetProperty("history_to").GetString().Should().Be("2026-09-30");
        joint.GetProperty("latest_balance").GetProperty("amount").GetString().Should().Be("1234.50");
        joint.GetProperty("balance_reconciles").GetString().Should().Be("yes");
        joint.GetProperty("last_successful_sync").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Every_access_token_carries_exactly_the_mcp_resource_as_its_audience()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();

        var (_, tokens) = await host.SignInAsync(login, ClientRegistrations.HostedClientId);

        using var scope = host.Factory.Services.CreateScope();
        var validation = scope.ServiceProvider.GetRequiredService<OpenIddictValidationService>();
        var principal = await validation.ValidateAccessTokenAsync(tokens.AccessToken!, TestContext.Current.CancellationToken);

        principal.GetAudiences().Should().Equal("https://mcp.example.com/mcp");
        principal.HasScope("ledger.read").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_request_for_any_other_resource_is_refused_before_sign_in()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();

        var outcome = await driver.AuthorizeAsync(
            discovery,
            ClientRegistrations.CodeClientId,
            login,
            OAuthTestDriver.LoopbackRedirectUri,
            resource: "https://other.example.com/mcp");

        outcome.Code.Should().BeNull();
        outcome.Error.Should().Be("invalid_target");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Denying_on_the_consent_page_returns_access_denied_and_issues_no_code()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();

        var outcome = await driver.AuthorizeAsync(
            discovery,
            ClientRegistrations.HostedClientId,
            login,
            OAuthTestDriver.HostedRedirectUri,
            approve: false);

        outcome.Code.Should().BeNull();
        outcome.Error.Should().Be("access_denied");
        outcome.ConsentHtml.Should().Contain("claude.ai");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Only_the_two_claude_clients_exist_and_no_client_can_register_itself()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));

        using var scope = host.Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        var clientIds = new List<string?>();
        var redirectHosts = new HashSet<string>();

        await foreach (var application in manager.ListAsync(count: null, offset: null, TestContext.Current.CancellationToken))
        {
            clientIds.Add(await manager.GetClientIdAsync(application, TestContext.Current.CancellationToken));

            foreach (var uri in await manager.GetRedirectUrisAsync(application, TestContext.Current.CancellationToken))
            {
                uri.Should().NotContain("*", "a wildcard redirect address would let any page receive a code");
                redirectHosts.Add(new Uri(uri).Host);
            }

            (await manager.HasClientTypeAsync(application, OpenIddictConstants.ClientTypes.Public, TestContext.Current.CancellationToken))
                .Should().BeTrue();
        }

        clientIds.Should().BeEquivalentTo([ClientRegistrations.HostedClientId, ClientRegistrations.CodeClientId]);
        redirectHosts.Should().BeEquivalentTo(["claude.ai", "claude.com", "localhost", "127.0.0.1"]);

        using var browser = host.CreateBrowser();
        using var unknown = await browser.PostAsync(
            "/zzz-nothing-here",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var unknownStatus = unknown.StatusCode;

        foreach (var path in new[] { "/connect/register", "/register", "/connect/registration" })
        {
            using var response = await browser.PostAsync(
                path,
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(unknownStatus, $"{path} must answer like any address that is not mapped");
        }
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task With_no_synced_accounts_the_overview_is_empty_and_says_so()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);

        await using var host = await McpTestHost.StartAsync(connectionString);
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        var overview = await ReadOverviewAsync(connection);

        overview.GetProperty("accounts").GetArrayLength().Should().Be(0);
        overview.GetProperty("note").GetString().Should().Be("No accounts are synced yet.");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_overview_lists_accounts_in_creation_order_on_every_call()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await LedgerQuerySeed.SeedAccountsInOrderAsync(connectionString, "Zulu", "Alpha", "Mike");

        await using var host = await McpTestHost.StartAsync(connectionString);
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);

        for (var call = 0; call < 3; call++)
        {
            var overview = await ReadOverviewAsync(connection);

            overview.GetProperty("accounts").EnumerateArray()
                .Select(account => account.GetProperty("name").GetString())
                .Should().Equal("Zulu", "Alpha", "Mike");
        }
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Rest_endpoints_keep_the_api_key_challenge_on_an_oauth_enabled_host()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = host.Factory.CreateApiClient();

        using var response = await client.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("ApiKey");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Without_a_public_base_address_no_oauth_sign_in_or_mcp_endpoint_exists()
    {
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            enableOAuth: false);
        using var client = host.Factory.CreateApiClient();

        using var unknown = await client.PostAsync(
            "/zzz-nothing-here",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        foreach (var path in new[] { "/mcp", "/account/login", "/connect/authorize", "/connect/token", "/.well-known/oauth-authorization-server" })
        {
            using var response = await client.PostAsync(
                path,
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(unknown.StatusCode, $"{path} must answer like any address that is not mapped");
            response.Headers.WwwAuthenticate.ToString().Should().Be(
                unknown.Headers.WwwAuthenticate.ToString(),
                $"{path} must not start a bearer challenge");
        }

        unknown.Headers.WwwAuthenticate.ToString().Should().Contain("ApiKey");
    }

    private static async Task<JsonElement> ReadOverviewAsync(McpConnection connection)
    {
        var result = await connection.Client.CallToolAsync(
            "ledger_overview",
            cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
