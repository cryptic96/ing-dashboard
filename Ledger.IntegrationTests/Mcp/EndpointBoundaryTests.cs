using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository;
using Ledger.Repository.Stores;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Proves the app itself keeps the public host to the MCP and OAuth surface, keeps sign-in to the home and VPN networks, binds
/// tokens to one audience, keeps API keys and bearer tokens apart, limits rates and sizes, and makes no outgoing request while it
/// serves tool calls, whatever the reverse proxy in front of it does.
/// </summary>
[Collection("Database")]
public class EndpointBoundaryTests(DatabaseFixture fixture)
{
    private const string OutsideAddress = "203.0.113.7";
    private const string AnthropicAddress = "160.79.104.10";

    [Theory]
    [Trait("Category", "OAuth")]
    [InlineData("/api/v1/status")]
    [InlineData("/metrics")]
    [InlineData("/health")]
    [InlineData("/")]
    [InlineData("/api/v1/bank/connections")]
    [InlineData("/.well-known/jwks")]
    [InlineData("/mcp/extra")]
    public async Task The_public_host_answers_not_found_for_everything_outside_the_mcp_and_oauth_surface(string path)
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("Category", "OAuth")]
    [InlineData("/mcp")]
    [InlineData("/connect/token")]
    [InlineData("/connect/authorize")]
    [InlineData("/account/login")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task Any_other_host_answers_not_found_for_the_mcp_and_oauth_surface(string path)
    {
        await using var host = await StartHostAsync();
        using var client = host.Factory.CreateApiClient();

        using var get = await client.GetAsync(path, TestContext.Current.CancellationToken);
        using var post = await client.PostAsync(
            path,
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Another_host_name_that_is_not_the_public_one_never_carries_the_mcp_surface()
    {
        await using var host = await StartHostAsync();
        using var client = host.Factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        request.Headers.Host = "ledger-api.example.com";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_internal_api_still_answers_on_the_loopback_host_with_its_own_challenge()
    {
        await using var host = await StartHostAsync();
        using var client = host.Factory.CreateApiClient();

        using var response = await client.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("ApiKey");
    }

    [Theory]
    [Trait("Category", "OAuth")]
    [InlineData(OutsideAddress)]
    [InlineData(AnthropicAddress)]
    [InlineData("192.0.1.255")]
    [InlineData("192.0.3.0")]
    [InlineData("2001:db8::1")]
    public async Task Sign_in_and_consent_pages_answer_not_found_to_addresses_outside_the_home_networks(string clientAddress)
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser(clientAddress);
        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();
        var (authorizeAddress, _, _) = OAuthTestDriver.BuildAuthorizeAddress(
            discovery,
            ClientRegistrations.CodeClientId,
            OAuthTestDriver.LoopbackRedirectUri);

        using var login = await browser.GetAsync("/account/login", TestContext.Current.CancellationToken);
        using var totp = await browser.GetAsync("/account/totp", TestContext.Current.CancellationToken);
        using var loginPost = await browser.PostAsync(
            "/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["UserName"] = "x", ["Password"] = "y" }),
            TestContext.Current.CancellationToken);
        using var authorize = await browser.GetAsync(authorizeAddress, TestContext.Current.CancellationToken);

        login.StatusCode.Should().Be(HttpStatusCode.NotFound);
        totp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        loginPost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        authorize.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("Category", "OAuth")]
    [InlineData("192.0.2.50")]
    [InlineData("192.0.2.0")]
    [InlineData("192.0.2.255")]
    public async Task Sign_in_and_consent_pages_answer_to_addresses_inside_the_home_networks_including_both_ends(string clientAddress)
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser(clientAddress);
        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();
        var (authorizeAddress, _, _) = OAuthTestDriver.BuildAuthorizeAddress(
            discovery,
            ClientRegistrations.CodeClientId,
            OAuthTestDriver.LoopbackRedirectUri);

        using var login = await browser.GetAsync("/account/login", TestContext.Current.CancellationToken);
        using var authorize = await browser.GetAsync(authorizeAddress, TestContext.Current.CancellationToken);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, "an unauthenticated authorize request goes to the sign-in page");
        authorize.Headers.Location!.ToString().Should().Contain("/account/login");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Discovery_the_token_endpoint_and_the_mcp_endpoint_stay_reachable_from_any_address()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser(AnthropicAddress);

        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();
        using var token = await browser.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code" }),
            TestContext.Current.CancellationToken);

        discovery.Issuer.Should().Be("https://mcp.example.com/");
        token.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_caller_on_the_loopback_address_is_not_a_trusted_proxy_unless_it_is_configured_as_one()
    {
        await using var host = await McpTestHost.StartAsync(
            fixture.ConnectionStringFor("ledger_runtime"),
            new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "192.0.2.10" });
        using var browser = host.CreateBrowser("192.0.2.50");

        using var response = await browser.GetAsync("/account/login", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the forwarded client address of an untrusted caller is ignored, so loopback is judged");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_plain_loopback_probe_with_the_public_host_name_gets_the_challenge_but_no_status_endpoint_or_sign_in_page()
    {
        await using var host = await StartHostAsync();
        using var client = host.Factory.CreateApiClient();

        using var challenge = await SendProbeAsync(client, HttpMethod.Post, "/mcp");
        using var status = await SendProbeAsync(client, HttpMethod.Get, "/api/v1/status");
        using var signIn = await SendProbeAsync(client, HttpMethod.Get, "/account/login");

        challenge.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string.Join(", ", challenge.Headers.WwwAuthenticate.Select(value => value.ToString()))
            .Should().Contain("resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource/mcp\"");
        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
        signIn.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_client_address_the_caller_prepends_to_the_forwarded_chain_is_ignored()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser("192.0.2.50, " + OutsideAddress);

        using var response = await browser.GetAsync("/account/login", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "only the address the proxy appended counts");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_resource_that_is_not_the_mcp_address_is_refused_and_every_canonical_spelling_is_accepted()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();
        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();

        foreach (var foreign in new[] { "https://mcp.example.com/other", "https://mcp.example.com/mcp2", "https://mcp.example.com/mcp/x", "https://mcp.example.com/mcp#part", "http://mcp.example.com/mcp" })
        {
            var (status, location) = await AuthorizeStatusAsync(browser, discovery, foreign);

            location.Should().NotContain("/account/login", foreign);
            (status == HttpStatusCode.BadRequest || location.Contains("error=invalid_target", StringComparison.Ordinal))
                .Should().BeTrue($"{foreign} must be refused, got {status} {location}");
        }

        foreach (var spelling in new[] { "HTTPS://MCP.example.com/mcp", "https://mcp.example.com:443/mcp", "https://mcp.example.com/mcp/" })
        {
            (await AuthorizeStatusAsync(browser, discovery, spelling)).Location.Should().Contain("/account/login", spelling);
        }
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task When_several_resources_are_sent_each_must_be_the_mcp_address()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();
        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();

        var bothCanonical = await AuthorizeStatusAsync(browser, discovery, "https://mcp.example.com/mcp", "https://MCP.example.com/mcp/");
        var oneForeign = await AuthorizeStatusAsync(browser, discovery, "https://mcp.example.com/mcp", "https://other.example.com/mcp");

        bothCanonical.Location.Should().Contain("/account/login");
        oneForeign.Location.Should().Contain("error=invalid_target");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_protected_resource_metadata_lists_exactly_one_authorization_server_equal_to_the_issuer()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();

        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();

        var servers = discovery.ResourceMetadata.GetProperty("authorization_servers").EnumerateArray().Select(server => server.GetString()).ToList();
        servers.Should().ContainSingle().Which.Should().Be(discovery.AuthorizationServerMetadata.GetProperty("issuer").GetString());
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_token_issued_by_another_instance_for_another_resource_is_rejected_at_the_mcp_endpoint()
    {
        var connectionString = fixture.ConnectionStringFor("ledger_runtime");
        await using var first = await StartHostAsync();
        await using var other = await McpTestHost.StartAsync(
            connectionString,
            new Dictionary<string, string?> { ["OAuth:PublicBaseUrl"] = "https://other.example.com" });
        var login = await other.CreateLoginAsync();

        var (_, tokens) = await other.SignInAsync(login);

        (await other.ToolsListStatusAsync(tokens.AccessToken)).Should().Be(HttpStatusCode.OK, "the token works where it was issued");
        (await first.ToolsListStatusAsync(tokens.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task An_api_key_never_authenticates_the_mcp_endpoint_and_a_bearer_token_never_authenticates_rest()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        var (_, tokens) = await host.SignInAsync(login);
        var apiKey = await CreateApiKeyAsync();

        using var browser = host.CreateBrowser();
        using var keyOnMcp = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json")
        };
        keyOnMcp.Headers.Add("X-Api-Key", apiKey);
        keyOnMcp.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        keyOnMcp.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var mcpResponse = await browser.SendAsync(keyOnMcp, TestContext.Current.CancellationToken);

        using var rest = host.Factory.CreateApiClient();
        rest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var restResponse = await rest.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        using var keyOnRest = host.Factory.CreateApiClient();
        keyOnRest.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        using var keyOnRestResponse = await keyOnRest.GetAsync("/api/v1/status", TestContext.Current.CancellationToken);

        mcpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mcpResponse.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer").And.NotContain("ApiKey");
        restResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        restResponse.Headers.WwwAuthenticate.ToString().Should().Contain("ApiKey");
        keyOnRestResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the key itself is valid where it belongs");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Every_rest_endpoint_on_an_oauth_enabled_host_still_requires_a_key()
    {
        await using var host = await StartHostAsync();
        using var client = host.Factory.CreateApiClient();

        var endpoints = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();

        endpoints.Should().NotBeEmpty();

        foreach (var endpoint in endpoints)
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var path = System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText!, "\\{[^}]+\\}", "placeholder");

            using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} needs an API key");
            response.Headers.WwwAuthenticate.ToString().Should().Contain("ApiKey");
        }
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_hosted_client_receives_its_code_at_its_registered_callback_and_can_exchange_it()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();

        var outcome = await driver.AuthorizeAsync(discovery, ClientRegistrations.HostedClientId, login, OAuthTestDriver.HostedRedirectUri);
        var tokens = await driver.ExchangeAsync(discovery, ClientRegistrations.HostedClientId, outcome);

        outcome.Location.Should().StartWith("https://claude.ai/api/mcp/auth_callback?");
        outcome.Code.Should().NotBeNullOrEmpty();
        tokens.Succeeded.Should().BeTrue();
    }

    [Theory]
    [Trait("Category", "OAuth")]
    [InlineData("https://evil.example.org/callback")]
    [InlineData("https://claude.ai/other")]
    [InlineData("https://claude.ai.example.org/api/mcp/auth_callback")]
    [InlineData("http://claude.ai/api/mcp/auth_callback")]
    public async Task A_redirect_address_that_is_not_registered_gets_an_error_page_and_is_never_redirected_to(string redirectUri)
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();
        var discovery = await new OAuthTestDriver(browser).DiscoverAsync();
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(discovery, ClientRegistrations.HostedClientId, redirectUri);

        using var response = await browser.GetAsync(address, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect);
        response.Headers.Location.Should().BeNull();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task No_outgoing_request_leaves_the_app_while_it_serves_tool_calls()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        await using var connection = await host.ConnectAsync(login);
        var requests = new ConcurrentBag<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => requests.Add($"{activity.GetTagItem("url.full")}|{activity.GetTagItem("server.port")}")
        };
        ActivitySource.AddActivityListener(listener);

        for (var call = 0; call < 3; call++)
        {
            var result = await connection.Client.CallToolAsync("ledger_overview", cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.Should().NotBe(true);
        }

        requests.Should().NotBeEmpty("the listener must see the test client's own calls");
        requests.Should().OnlyContain(target => target.EndsWith($"|{host.Factory.ApiPort}", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_eleventh_sign_in_post_within_a_minute_from_one_address_is_refused_and_another_address_is_not_affected()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();
        using var other = host.CreateBrowser("192.0.2.51");

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await PostLoginFormAsync(browser);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, $"post {attempt} is inside the limit");
        }

        using var refused = await PostLoginFormAsync(browser);
        using var unaffected = await PostLoginFormAsync(other);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        unaffected.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_thirty_first_token_request_within_a_minute_from_one_address_is_refused()
    {
        await using var host = await StartHostAsync();
        using var browser = host.CreateBrowser();

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            using var response = await PostTokenAsync(browser);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, $"request {attempt} is inside the limit");
        }

        using var refused = await PostTokenAsync(browser);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_three_hundred_and_first_mcp_request_within_a_minute_from_one_address_is_refused()
    {
        await using var host = await StartHostAsync();

        for (var attempt = 1; attempt <= 300; attempt++)
        {
            (await host.ToolsListStatusAsync(null)).Should().Be(HttpStatusCode.Unauthorized, $"request {attempt} is inside the limit");
        }

        (await host.ToolsListStatusAsync(null)).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_request_body_above_256_KiB_is_refused()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        var (_, tokens) = await host.SignInAsync(login);
        using var browser = host.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"padding\":\"" + new string('x', 300 * 1024) + "\"}}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await browser.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    private async Task<McpTestHost> StartHostAsync()
    {
        return await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
    }

    private static async Task<HttpResponseMessage> SendProbeAsync(HttpClient client, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Host = "mcp.example.com";

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostLoginFormAsync(HttpClient browser)
    {
        return await browser.PostAsync(
            "/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["UserName"] = "x", ["Password"] = "y" }),
            TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostTokenAsync(HttpClient browser)
    {
        return await browser.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code" }),
            TestContext.Current.CancellationToken);
    }

    private static async Task<(HttpStatusCode Status, string Location)> AuthorizeStatusAsync(
        HttpClient browser,
        DiscoveryDocuments discovery,
        params string[] resources)
    {
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(
            discovery,
            ClientRegistrations.CodeClientId,
            OAuthTestDriver.LoopbackRedirectUri,
            resources[0]);

        foreach (var extra in resources.Skip(1))
        {
            address += "&resource=" + Uri.EscapeDataString(extra);
        }

        using var response = await browser.GetAsync(address, TestContext.Current.CancellationToken);

        return (response.StatusCode, response.Headers.Location?.ToString() ?? string.Empty);
    }

    private async Task<string> CreateApiKeyAsync()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(fixture.ConnectionStringFor("ledger_runtime"))
            .Options;
        await using var context = new LedgerDbContext(options);
        var created = await new ApiKeyStore(context).CreateAsync("boundary-test-" + Guid.NewGuid().ToString("N")[..8], TestContext.Current.CancellationToken);

        return created.Token;
    }
}
