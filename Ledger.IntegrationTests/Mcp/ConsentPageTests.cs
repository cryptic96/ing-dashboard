using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.OAuth;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Proves the consent page's refusal cannot be overridden: a link that smuggles a decision into the request, or a form that
/// carries the decision twice, never approves, and the only fields the page hands back are the OAuth request parameters.
/// </summary>
[Collection("Database")]
public class ConsentPageTests(DatabaseFixture fixture)
{
    private const string ClientId = ClientRegistrations.CodeClientId;

    [Theory]
    [InlineData("decision=approve")]
    [InlineData("Decision=approve")]
    [InlineData("DECISION=approve")]
    [InlineData("dEcIsIoN=approve")]
    [Trait("Category", "OAuth")]
    public async Task A_decision_smuggled_into_the_authorize_link_cannot_override_the_deny_button(string smuggled)
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();

        var outcome = await driver.AuthorizeAsync(
            discovery,
            ClientId,
            login,
            OAuthTestDriver.LoopbackRedirectUri,
            approve: false,
            extraQuery: smuggled);

        outcome.Error.Should().Be("access_denied");
        outcome.Code.Should().BeNull();
        HiddenDecisionField().IsMatch(outcome.ConsentHtml).Should().BeFalse("the page must not hand a decision back as a hidden field");
        (await ActiveGrantsAsync(host, login)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_form_that_carries_the_decision_twice_is_read_as_a_refusal()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var consent = await OpenConsentPageAsync(driver, browser, discovery, login);

        var fields = OAuthTestDriver.HiddenFields(consent.Html)
            .Select(field => new KeyValuePair<string, string>(field.Key, field.Value))
            .Concat([new("Decision", "approve"), new("decision", "deny")]);

        using var decided = await browser.PostAsync(
            discovery.AuthorizationEndpoint,
            new FormUrlEncodedContent(fields),
            TestContext.Current.CancellationToken);

        decided.StatusCode.Should().Be(HttpStatusCode.Redirect);
        decided.Headers.Location!.ToString().Should().Contain("error=access_denied").And.NotContain("code=");
        (await ActiveGrantsAsync(host, login)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_consent_page_hands_back_only_the_oauth_request_parameters()
    {
        await using var host = await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var consent = await OpenConsentPageAsync(driver, browser, discovery, login, "unrelated=1&__RequestVerificationToken=x");

        var names = OAuthTestDriver.HiddenFields(consent.Html).Keys;

        names.Should().Contain(["client_id", "redirect_uri", "response_type", "code_challenge", "code_challenge_method", "state", "resource", "scope"]);
        names.Should().NotContain("unrelated");
        names.Count(name => name.Equals("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "only the page's own token is present");
    }

    private static async Task<(string Html, string Address)> OpenConsentPageAsync(
        OAuthTestDriver driver,
        HttpClient browser,
        DiscoveryDocuments discovery,
        TestLogin login,
        string? extraQuery = null)
    {
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(discovery, ClientId, OAuthTestDriver.LoopbackRedirectUri);

        if (extraQuery is not null)
        {
            address += "&" + extraQuery;
        }

        using var first = await browser.GetAsync(address, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var password = await driver.PostPasswordAsync(first.Headers.Location!.ToString(), login.UserName, login.Password);
        using var signedIn = await driver.PostCodeAsync(password.Headers.Location!.ToString(), login.NextCode());
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var consent = await browser.GetAsync(signedIn.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        consent.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await consent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), address);
    }

    private static async Task<int> ActiveGrantsAsync(McpTestHost host, TestLogin login)
    {
        using var scope = host.Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<GrantRevocationService>().CountActiveGrantsAsync(login.Id);
    }

    private static Regex HiddenDecisionField() =>
        new("<input[^>]*type=\"hidden\"[^>]*name=\"decision\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
