using System.Net;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.IntegrationTests.Mcp;
using Ledger.Repository.Entities;
using Ledger.Repository.Stores;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Security;

/// <summary>
/// Proves a login's authenticator secret is stored encrypted, still verifies codes, and that a stored value this host cannot
/// decrypt locks the second factor closed instead of leaking anything.
/// </summary>
[Collection("Database")]
public class AuthenticatorKeyProtectionTests(DatabaseFixture fixture)
{
    private const string GenericMessage = "Sign-in failed.";

    [Fact]
    [Trait("Category", "Security")]
    public async Task The_stored_authenticator_secret_is_encrypted_and_reads_back_as_the_original()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);

        var login = await host.CreateLoginAsync();
        var stored = await AuthenticatorKeyStore.ReadRawAsync(connectionString, login.UserName);

        stored.Should().NotBeNullOrEmpty().And.NotContain(login.AuthenticatorKey);
        stored.Should().NotBe(login.AuthenticatorKey);
        using var scope = host.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LedgerUserEntity>>();
        var user = await users.FindByIdAsync(login.Id.ToString());
        (await users.GetAuthenticatorKeyAsync(user!)).Should().Be(login.AuthenticatorKey);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A_full_sign_in_with_the_password_and_the_current_code_still_works()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var login = await host.CreateLoginAsync();

        await using var connection = await host.ConnectAsync(login);

        (await host.ToolsListStatusAsync(connection.AccessToken)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Enrolment_never_writes_recovery_codes()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);

        var login = await host.CreateLoginAsync();
        var names = await AuthenticatorKeyStore.TokenNamesAsync(connectionString, login.UserName);

        names.Should().ContainSingle().Which.Should().Be("AuthenticatorKey");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A_plaintext_secret_cannot_complete_the_second_factor_and_the_refusal_is_generic()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var login = await host.CreateLoginAsync();

        (await AuthenticatorKeyStore.WriteRawAsync(connectionString, login.UserName, login.AuthenticatorKey)).Should().Be(1);

        await AssertSecondFactorIsRefusedAsync(host, login, login.AuthenticatorKey);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A_secret_protected_by_another_key_ring_cannot_complete_the_second_factor_and_the_refusal_is_generic()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var login = await host.CreateLoginAsync();
        var foreign = new EphemeralDataProtectionProvider()
            .CreateProtector(LedgerUserStore.ProtectorPurpose)
            .Protect(login.AuthenticatorKey);

        (await AuthenticatorKeyStore.WriteRawAsync(connectionString, login.UserName, foreign)).Should().Be(1);

        await AssertSecondFactorIsRefusedAsync(host, login, foreign);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A_login_whose_secret_row_is_missing_is_refused_at_the_password_and_never_signed_in()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var login = await host.CreateLoginAsync();
        (await AuthenticatorKeyStore.DeleteRawAsync(connectionString, login.UserName)).Should().Be(1);

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(discovery, ClientRegistrations.CodeClientId, OAuthTestDriver.LoopbackRedirectUri);
        using var first = await browser.GetAsync(address, TestContext.Current.CancellationToken);

        using var attempt = await driver.PostPasswordAsync(first.Headers.Location!.ToString(), login.UserName, login.Password);

        attempt.StatusCode.Should().Be(HttpStatusCode.OK, "the sign-in page is shown again");
        (await attempt.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(GenericMessage);
        using var afterwards = await browser.GetAsync(address, TestContext.Current.CancellationToken);
        afterwards.StatusCode.Should().Be(HttpStatusCode.Redirect);
        afterwards.Headers.Location!.ToString().Should().Contain("/account/login", "no sign-in cookie may have been issued");
    }

    private static Task<McpTestHost> StartHostAsync(string connectionString) =>
        McpTestHost.StartAsync(connectionString, TestCertificates.SharedKeyRingSettings);

    private static async Task AssertSecondFactorIsRefusedAsync(McpTestHost host, TestLogin login, string storedValue)
    {
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(discovery, ClientRegistrations.CodeClientId, OAuthTestDriver.LoopbackRedirectUri);

        using var first = await browser.GetAsync(address, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var password = await driver.PostPasswordAsync(first.Headers.Location!.ToString(), login.UserName, login.Password);
        password.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var code = await driver.PostCodeAsync(password.Headers.Location!.ToString(), login.NextCode());

        code.StatusCode.Should().Be(HttpStatusCode.OK, "the code page is shown again instead of continuing");
        var body = await code.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(GenericMessage).And.NotContain(login.AuthenticatorKey).And.NotContain(storedValue);

        foreach (var message in host.Factory.CapturedLogMessages)
        {
            message.Should().NotContain(login.AuthenticatorKey).And.NotContain(storedValue);
        }
    }
}
