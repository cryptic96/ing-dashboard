using System.Net;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.OAuth;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Proves sign-in needs the password and a fresh one-time code, that a replayed or guessed code gets nowhere, and that the
/// sign-in and consent pages carry the headers and tokens that protect the form.
/// </summary>
[Collection("Database")]
public class SignInTests(DatabaseFixture fixture)
{
    private const string GenericMessage = "Sign-in failed.";

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_login_without_a_second_factor_cannot_sign_in_and_never_reaches_the_consent_page()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync(enrolSecondFactor: false);
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var (authorizeAddress, loginAddress) = await StartAuthorizationAsync(driver, browser);

        using var attempt = await driver.PostPasswordAsync(loginAddress, login.UserName, login.Password);

        attempt.StatusCode.Should().Be(HttpStatusCode.OK, "the page is shown again, not a redirect");
        var body = await attempt.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(GenericMessage).And.NotContain(login.UserName);

        using var consent = await browser.GetAsync(authorizeAddress, TestContext.Current.CancellationToken);
        consent.StatusCode.Should().Be(HttpStatusCode.Redirect);
        consent.Headers.Location!.ToString().Should().Contain("/account/login");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_password_then_the_current_code_reach_the_consent_page()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var (authorizeAddress, loginAddress) = await StartAuthorizationAsync(driver, browser);

        using var password = await driver.PostPasswordAsync(loginAddress, login.UserName, login.Password);
        password.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var code = await driver.PostCodeAsync(password.Headers.Location!.ToString(), login.NextCode());
        code.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var consent = await browser.GetAsync(authorizeAddress, TestContext.Current.CancellationToken);
        consent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await consent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("Allow access");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_code_that_was_already_accepted_is_refused_when_presented_again()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        var code = login.NextCode();

        using var first = host.CreateBrowser();
        (await SignInAsync(first, login, code)).Should().Be(HttpStatusCode.Redirect);

        using var second = host.CreateBrowser();
        (await SignInAsync(second, login, code)).Should().Be(HttpStatusCode.OK, "the same code must not work twice");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Two_simultaneous_submissions_of_one_fresh_code_yield_exactly_one_success()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        var code = login.NextCode();

        using var first = host.CreateBrowser();
        using var second = host.CreateBrowser();

        var results = await Task.WhenAll(SignInAsync(first, login, code), SignInAsync(second, login, code));

        results.Count(status => status == HttpStatusCode.Redirect).Should().Be(1);
        results.Count(status => status == HttpStatusCode.OK).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_code_from_ten_minutes_ago_is_refused()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        var stale = TotpCode.Compute(login.AuthenticatorKey, DateTimeOffset.UtcNow.AddMinutes(-10));

        using var browser = host.CreateBrowser();

        (await SignInAsync(browser, login, stale)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task Five_wrong_codes_lock_the_login_and_a_correct_attempt_afterwards_fails_with_the_same_message()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var (_, loginAddress) = await StartAuthorizationAsync(driver, browser);

        using var password = await driver.PostPasswordAsync(loginAddress, login.UserName, login.Password);
        var codeAddress = password.Headers.Location!.ToString();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await driver.PostCodeAsync(codeAddress, TotpCode.Wrong(login.AuthenticatorKey, DateTimeOffset.UtcNow));
            wrong.StatusCode.Should().Be(HttpStatusCode.OK);
            (await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(GenericMessage);
        }

        using var rightCode = await driver.PostCodeAsync(codeAddress, login.NextCode());
        rightCode.StatusCode.Should().Be(HttpStatusCode.OK, "a locked login cannot complete sign-in");
        (await rightCode.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(GenericMessage);

        using var fresh = host.CreateBrowser();
        var freshDriver = new OAuthTestDriver(fresh);
        var (_, freshLoginAddress) = await StartAuthorizationAsync(freshDriver, fresh);
        using var again = await freshDriver.PostPasswordAsync(freshLoginAddress, login.UserName, login.Password);

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(GenericMessage);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task An_unknown_login_and_a_wrong_password_show_the_same_message()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var (_, loginAddress) = await StartAuthorizationAsync(driver, browser);

        using var unknown = await driver.PostPasswordAsync(loginAddress, "nobody-" + Guid.NewGuid().ToString("N")[..8], login.Password);
        using var wrong = await driver.PostPasswordAsync(loginAddress, login.UserName, login.Password + "x");

        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        wrong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(GenericMessage);
        (await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(GenericMessage);
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task A_login_post_without_the_form_token_is_refused_and_an_outside_return_address_is_ignored()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();

        using var forged = await browser.PostAsync(
            "/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["UserName"] = login.UserName,
                ["Password"] = login.Password
            }),
            TestContext.Current.CancellationToken);

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var driver = new OAuthTestDriver(browser);
        var address = "/account/login?returnUrl=" + Uri.EscapeDataString("https://example.org/x");
        using var password = await driver.PostPasswordAsync(address, login.UserName, login.Password);
        password.StatusCode.Should().Be(HttpStatusCode.Redirect);
        password.Headers.Location!.ToString().Should().NotContain("example.org");

        using var code = await driver.PostCodeAsync(password.Headers.Location.ToString(), login.NextCode());

        code.StatusCode.Should().Be(HttpStatusCode.OK, "an address outside this host is never followed");
        (await code.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("You are signed in");
    }

    [Fact]
    [Trait("Category", "OAuth")]
    public async Task The_login_and_consent_pages_are_never_cached_and_allow_only_the_clients_own_form_targets()
    {
        await using var host = await StartHostAsync();
        var login = await host.CreateLoginAsync();
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var (authorizeAddress, _, _) = OAuthTestDriver.BuildAuthorizeAddress(
            discovery,
            ClientRegistrations.HostedClientId,
            OAuthTestDriver.HostedRedirectUri);

        using var first = await browser.GetAsync(authorizeAddress, TestContext.Current.CancellationToken);
        var loginAddress = first.Headers.Location!.ToString();

        using var loginPage = await browser.GetAsync(loginAddress, TestContext.Current.CancellationToken);
        AssertStrictHeaders(loginPage, "form-action 'self';");

        using var password = await driver.PostPasswordAsync(loginAddress, login.UserName, login.Password);
        using var code = await driver.PostCodeAsync(password.Headers.Location!.ToString(), login.NextCode());
        code.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var consent = await browser.GetAsync(authorizeAddress, TestContext.Current.CancellationToken);
        consent.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertStrictHeaders(consent, "form-action 'self' https://claude.ai https://claude.com;");

        var html = await consent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.Should().Contain("Claude (claude.ai").And.Contain("claude.ai").And.Contain("__RequestVerificationToken");
        html.Should().NotContain("<script");
    }

    private async Task<McpTestHost> StartHostAsync()
    {
        return await McpTestHost.StartAsync(fixture.ConnectionStringFor("ledger_runtime"));
    }

    private static async Task<(string AuthorizeAddress, string LoginAddress)> StartAuthorizationAsync(OAuthTestDriver driver, HttpClient browser)
    {
        var discovery = await driver.DiscoverAsync();
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(
            discovery,
            ClientRegistrations.CodeClientId,
            OAuthTestDriver.LoopbackRedirectUri);

        using var response = await browser.GetAsync(address, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        return (address, response.Headers.Location!.ToString());
    }

    private static async Task<HttpStatusCode> SignInAsync(HttpClient browser, TestLogin login, string code)
    {
        var driver = new OAuthTestDriver(browser);
        var (_, loginAddress) = await StartAuthorizationAsync(driver, browser);

        using var password = await driver.PostPasswordAsync(loginAddress, login.UserName, login.Password);
        password.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var result = await driver.PostCodeAsync(password.Headers.Location!.ToString(), code);

        return result.StatusCode;
    }

    private static void AssertStrictHeaders(HttpResponseMessage response, string expectedFormAction)
    {
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var policy = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        policy.Should().Contain("default-src 'none'").And.Contain("frame-ancestors 'none'").And.Contain(expectedFormAction);
        policy.Should().NotContain("script-src");
    }
}
