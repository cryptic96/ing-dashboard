using System.Net;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Cli;
using Ledger.Service.OAuth;
using Ledger.Repository.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Mcp;

/// <summary>
/// Proves the operator's two commands on the real database and the real host: enrolling a login that signs in only after its
/// second factor is confirmed, and cutting Claude's access off, one grant or all of it, with effect on the very next request.
/// </summary>
[Collection("Database")]
public class OperatorCommandTests(DatabaseFixture fixture)
{
    private const string ClientId = ClientRegistrations.CodeClientId;

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_created_login_signs_in_only_after_its_authenticator_code_is_confirmed()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var name = NewName();
        var password = NewPassword();

        var created = await RunLoginAsync(connectionString, password, "create", name);

        created.ExitCode.Should().Be(0);
        var lines = Lines(created.Stdout);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith($"otpauth://totp/Household%20Ledger:{name}?secret=")
            .And.Contain("&issuer=Household%20Ledger").And.Contain("&digits=6").And.Contain("&period=30");
        var secret = lines[1];
        secret.Should().MatchRegex("^[A-Z2-7]{32}$");
        lines[0].Should().Contain($"secret={secret}");
        created.Stderr.Should().Contain("shown once").And.Contain("confirm-totp");
        created.Stdout.Should().NotContain(password);
        created.Stderr.Should().NotContain(password);

        var login = new TestLogin(Guid.Empty, name, password, secret);
        await AssertPasswordIsRefusedAsync(host, login);

        var wrong = await RunLoginAsync(connectionString, TotpCode.Wrong(secret, DateTimeOffset.UtcNow), "confirm-totp", name);
        wrong.ExitCode.Should().Be(1);
        await AssertPasswordIsRefusedAsync(host, login);

        var confirmedAt = DateTimeOffset.UtcNow;
        var confirmed = await RunLoginAsync(connectionString, TotpCode.Compute(secret, confirmedAt), "confirm-totp", name);
        confirmed.ExitCode.Should().Be(0);
        login.NoteCodeUsedAt(confirmedAt);

        await using var connection = await host.ConnectAsync(login, ClientId);
        (await host.ToolsListStatusAsync(connection.AccessToken)).Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("a")]
    [InlineData("x;y")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Trait("Category", "OperatorCommands")]
    public async Task Create_refuses_a_name_outside_the_pattern_and_creates_nothing(string name)
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);

        var created = await RunLoginAsync(connectionString, NewPassword(), "create", name);
        var listed = await RunLoginAsync(connectionString, string.Empty, "list");

        created.ExitCode.Should().Be(1);
        created.Stdout.Should().BeEmpty();
        Lines(listed.Stdout).Should().HaveCount(1, "only the header line remains");
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Create_refuses_an_empty_or_short_password_and_an_existing_name_without_changing_anything()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        var name = NewName();

        (await RunLoginAsync(connectionString, string.Empty, "create", name)).ExitCode.Should().Be(1);
        (await RunLoginAsync(connectionString, "too short", "create", name)).ExitCode.Should().Be(1);
        Lines((await RunLoginAsync(connectionString, string.Empty, "list")).Stdout).Should().HaveCount(1);

        var first = await RunLoginAsync(connectionString, NewPassword(), "create", name);
        first.ExitCode.Should().Be(0);
        var secret = Lines(first.Stdout)[1];

        var duplicate = await RunLoginAsync(connectionString, NewPassword(), "create", name);
        duplicate.ExitCode.Should().Be(1);
        duplicate.Stdout.Should().BeEmpty();

        var confirmed = await RunLoginAsync(connectionString, TotpCode.Compute(secret, DateTimeOffset.UtcNow), "confirm-totp", name);
        confirmed.ExitCode.Should().Be(0, "the original authenticator key must be untouched by the refused duplicate");
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Usage_errors_exit_with_two()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);

        (await RunLoginAsync(connectionString, string.Empty)).ExitCode.Should().Be(2);
        (await RunLoginAsync(connectionString, string.Empty, "create")).ExitCode.Should().Be(2);
        (await RunLoginAsync(connectionString, string.Empty, "explode", "someone")).ExitCode.Should().Be(2);
        (await RunLoginAsync(connectionString, string.Empty, "confirm-totp", "someone", "123456")).ExitCode.Should().Be(2, "a code is never an argument");
        (await RunGrantsAsync(connectionString)).ExitCode.Should().Be(2);
        (await RunGrantsAsync(connectionString, "explode")).ExitCode.Should().Be(2);
        (await RunGrantsAsync(connectionString, "revoke")).ExitCode.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task List_shows_the_second_factor_state_and_the_active_grants_and_never_a_secret()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (confirmed, confirmedPassword) = await CreateLoginAsync(connectionString, confirm: true);
        var (pending, pendingPassword) = await CreateLoginAsync(connectionString, confirm: false);

        await using var connection = await host.ConnectAsync(confirmed, ClientId);
        var listed = await RunLoginAsync(connectionString, string.Empty, "list");

        listed.ExitCode.Should().Be(0);
        var lines = Lines(listed.Stdout);
        lines[0].Should().Be("name\tcreated\tsecond_factor\tlocked_until\tactive_grants");
        var confirmedRow = lines.Single(line => line.StartsWith(confirmed.UserName + "\t", StringComparison.Ordinal)).Split('\t');
        confirmedRow[2].Should().Be("yes");
        confirmedRow[3].Should().Be("-");
        confirmedRow[4].Should().Be("1");
        var pendingRow = lines.Single(line => line.StartsWith(pending.UserName + "\t", StringComparison.Ordinal)).Split('\t');
        pendingRow[2].Should().Be("no");
        pendingRow[4].Should().Be("0");

        foreach (var secret in new[] { confirmedPassword, pendingPassword, confirmed.AuthenticatorKey, pending.AuthenticatorKey, connection.AccessToken, connection.RefreshToken })
        {
            listed.Stdout.Should().NotContain(secret);
            listed.Stderr.Should().NotContain(secret);
        }
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Grants_list_names_the_grant_and_its_client_and_never_prints_a_token()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);

        await using var connection = await host.ConnectAsync(login, ClientId);
        var listed = await RunGrantsAsync(connectionString, "list");

        listed.ExitCode.Should().Be(0);
        var lines = Lines(listed.Stdout);
        lines[0].Should().Be("grant_id\tclient\tlogin\tcreated\tstatus\tlive_tokens");
        lines.Should().HaveCount(2);
        var row = lines[1].Split('\t');
        row[0].Should().MatchRegex("^[0-9a-f-]{36}$");
        row[1].Should().Be(ClientId);
        row[2].Should().Be(login.UserName);
        DateTimeOffset.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        row[4].Should().Be("valid");
        int.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
        listed.Stdout.Should().NotContain(connection.AccessToken).And.NotContain(connection.RefreshToken);
        listed.Stderr.Should().NotContain(connection.AccessToken).And.NotContain(connection.RefreshToken);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Revoke_all_refuses_the_very_next_call_and_the_next_refresh_of_every_earlier_token()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (first, _) = await CreateLoginAsync(connectionString, confirm: true);
        var (second, _) = await CreateLoginAsync(connectionString, confirm: true);

        await using var firstConnection = await host.ConnectAsync(first, ClientId);
        await using var secondConnection = await host.ConnectAsync(second, ClientRegistrations.HostedClientId);
        (await host.ToolsListStatusAsync(firstConnection.AccessToken)).Should().Be(HttpStatusCode.OK);
        (await host.ToolsListStatusAsync(secondConnection.AccessToken)).Should().Be(HttpStatusCode.OK);

        var revoked = await RunGrantsAsync(connectionString, "revoke-all");

        revoked.ExitCode.Should().Be(0);
        revoked.Stderr.Should().MatchRegex(@"Revoked 2 grants and \d+ tokens\.");
        (await host.ToolsListStatusAsync(firstConnection.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await host.ToolsListStatusAsync(secondConnection.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var refreshed = await driver.RefreshAsync(discovery, ClientId, firstConnection.RefreshToken);
        refreshed.Succeeded.Should().BeFalse();
        refreshed.Error.Should().Be("invalid_grant");
        (await driver.RefreshAsync(discovery, ClientRegistrations.HostedClientId, secondConnection.RefreshToken)).Error.Should().Be("invalid_grant");

        var listed = await RunGrantsAsync(connectionString, "list");
        Lines(listed.Stdout).Skip(1).Should().OnlyContain(line => line.Contains("\trevoked\t") && line.EndsWith("\t0", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Revoke_one_grant_stops_only_that_grant_while_another_logins_grant_keeps_working()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (first, _) = await CreateLoginAsync(connectionString, confirm: true);
        var (second, _) = await CreateLoginAsync(connectionString, confirm: true);

        await using var firstConnection = await host.ConnectAsync(first, ClientId);
        await using var secondConnection = await host.ConnectAsync(second, ClientId);

        var listed = await RunGrantsAsync(connectionString, "list");
        var firstGrantId = Lines(listed.Stdout).Skip(1).Select(line => line.Split('\t')).Single(row => row[2] == first.UserName)[0];

        var unknown = await RunGrantsAsync(connectionString, "revoke", Guid.NewGuid().ToString());
        unknown.ExitCode.Should().Be(1);
        var malformed = await RunGrantsAsync(connectionString, "revoke", "not-a-grant");
        malformed.ExitCode.Should().Be(1);

        var revoked = await RunGrantsAsync(connectionString, "revoke", firstGrantId);

        revoked.ExitCode.Should().Be(0);
        revoked.Stderr.Should().MatchRegex(@"Revoked 1 grant and \d+ tokens?\.");
        (await host.ToolsListStatusAsync(firstConnection.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await host.ToolsListStatusAsync(secondConnection.AccessToken)).Should().Be(HttpStatusCode.OK);

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        (await driver.RefreshAsync(discovery, ClientId, firstConnection.RefreshToken)).Error.Should().Be("invalid_grant");
        (await driver.RefreshAsync(discovery, ClientId, secondConnection.RefreshToken)).Succeeded.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Set_password_replaces_the_password_and_revokes_the_logins_grants()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);
        var (bystander, _) = await CreateLoginAsync(connectionString, confirm: true);

        await using var connection = await host.ConnectAsync(login, ClientId);
        await using var bystanderConnection = await host.ConnectAsync(bystander, ClientId);

        var newPassword = NewPassword();
        var changed = await RunLoginAsync(connectionString, newPassword, "set-password", login.UserName);

        changed.ExitCode.Should().Be(0);
        changed.Stdout.Should().BeEmpty();
        changed.Stderr.Should().NotContain(newPassword).And.Contain("Revoked 1 grant");
        (await host.ToolsListStatusAsync(connection.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await host.ToolsListStatusAsync(bystanderConnection.AccessToken)).Should().Be(HttpStatusCode.OK);

        await AssertPasswordIsRefusedAsync(host, login);
        var replaced = login.WithPassword(newPassword);
        await using var again = await host.ConnectAsync(replaced, ClientId);
        (await host.ToolsListStatusAsync(again.AccessToken)).Should().Be(HttpStatusCode.OK);

        (await RunLoginAsync(connectionString, string.Empty, "set-password", login.UserName)).ExitCode.Should().Be(1);
        (await RunLoginAsync(connectionString, "too short", "set-password", login.UserName)).ExitCode.Should().Be(1);
        (await RunLoginAsync(connectionString, NewPassword(), "set-password", "nobody-here")).ExitCode.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Reset_totp_revokes_the_grants_and_the_login_cannot_sign_in_until_the_new_secret_is_confirmed()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, password) = await CreateLoginAsync(connectionString, confirm: true);

        await using var connection = await host.ConnectAsync(login, ClientId);

        var reset = await RunLoginAsync(connectionString, string.Empty, "reset-totp", login.UserName);

        reset.ExitCode.Should().Be(0);
        var lines = Lines(reset.Stdout);
        lines.Should().HaveCount(2);
        var newSecret = lines[1];
        newSecret.Should().NotBe(login.AuthenticatorKey).And.MatchRegex("^[A-Z2-7]{32}$");
        reset.Stderr.Should().Contain("Revoked 1 grant").And.Contain("confirm-totp");
        (await host.ToolsListStatusAsync(connection.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);

        var rekeyed = new TestLogin(Guid.Empty, login.UserName, password, newSecret);
        await AssertPasswordIsRefusedAsync(host, rekeyed);

        (await RunLoginAsync(connectionString, TotpCode.Compute(login.AuthenticatorKey, DateTimeOffset.UtcNow), "confirm-totp", login.UserName))
            .ExitCode.Should().Be(1, "the old authenticator no longer matches");
        var confirmedAt = DateTimeOffset.UtcNow;
        (await RunLoginAsync(connectionString, TotpCode.Compute(newSecret, confirmedAt), "confirm-totp", login.UserName))
            .ExitCode.Should().Be(0);
        rekeyed.NoteCodeUsedAt(confirmedAt);

        await using var again = await host.ConnectAsync(rekeyed, ClientId);
        (await host.ToolsListStatusAsync(again.AccessToken)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_code_that_confirmed_an_authenticator_cannot_be_used_again_on_the_command_or_the_web()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var name = NewName();
        var password = NewPassword();
        var created = await RunLoginAsync(connectionString, password, "create", name);
        var secret = Lines(created.Stdout)[1];
        var code = TotpCode.Compute(secret, DateTimeOffset.UtcNow);

        (await RunLoginAsync(connectionString, code, "confirm-totp", name)).ExitCode.Should().Be(0);

        var again = await RunLoginAsync(connectionString, code, "confirm-totp", name);
        again.ExitCode.Should().Be(1);
        again.Stderr.Should().NotContain(code);

        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(discovery, ClientId, OAuthTestDriver.LoopbackRedirectUri);
        using var first = await browser.GetAsync(address, TestContext.Current.CancellationToken);
        using var passwordPosted = await driver.PostPasswordAsync(first.Headers.Location!.ToString(), name, password);
        passwordPosted.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var attempt = await driver.PostCodeAsync(passwordPosted.Headers.Location!.ToString(), code);

        attempt.StatusCode.Should().Be(HttpStatusCode.OK, "the confirmation used the code up");
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_session_signed_in_before_set_password_cannot_approve_a_grant_afterwards()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var consent = await driver.OpenConsentPageAsync(discovery, ClientId, login, OAuthTestDriver.LoopbackRedirectUri);

        (await RunLoginAsync(connectionString, NewPassword(), "set-password", login.UserName)).ExitCode.Should().Be(0);

        await AssertDecisionIsSentToSignInAsync(host, driver, discovery, consent, login);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_session_signed_in_before_reset_totp_cannot_approve_a_grant_afterwards()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var consent = await driver.OpenConsentPageAsync(discovery, ClientId, login, OAuthTestDriver.LoopbackRedirectUri);

        (await RunLoginAsync(connectionString, string.Empty, "reset-totp", login.UserName)).ExitCode.Should().Be(0);

        await AssertDecisionIsSentToSignInAsync(host, driver, discovery, consent, login);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_session_signed_in_before_the_login_was_removed_cannot_approve_a_grant_afterwards()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var consent = await driver.OpenConsentPageAsync(discovery, ClientId, login, OAuthTestDriver.LoopbackRedirectUri);

        (await RunLoginAsync(connectionString, string.Empty, "remove", login.UserName)).ExitCode.Should().Be(0);

        using var decided = await driver.PostDecisionAsync(discovery, consent, "approve");

        decided.StatusCode.Should().Be(HttpStatusCode.Redirect);
        decided.Headers.Location!.ToString().Should().Contain("/account/login").And.NotContain("code=");
        (await RunGrantsAsync(connectionString, "list")).Stdout.Should().NotContain(ClientId);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_secret_written_by_the_command_is_stored_encrypted_and_a_login_it_enrolled_signs_in_through_the_web_host()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);

        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);
        var stored = await AuthenticatorKeyStore.ReadRawAsync(connectionString, login.UserName);

        stored.Should().NotBeNullOrEmpty().And.NotContain(login.AuthenticatorKey);
        await using var connection = await host.ConnectAsync(login, ClientId);
        (await host.ToolsListStatusAsync(connection.AccessToken)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task A_secret_written_by_the_web_host_is_read_by_the_command()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var enrolled = await host.CreateLoginAsync(enrolSecondFactor: true);

        var confirmed = await RunLoginAsync(connectionString, TotpCode.Compute(enrolled.AuthenticatorKey, DateTimeOffset.UtcNow), "confirm-totp", enrolled.UserName);
        var wrong = await RunLoginAsync(connectionString, TotpCode.Wrong(enrolled.AuthenticatorKey, DateTimeOffset.UtcNow), "confirm-totp", enrolled.UserName);

        confirmed.ExitCode.Should().Be(0);
        wrong.ExitCode.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "OperatorCommands")]
    public async Task Remove_revokes_the_logins_grants_deletes_it_and_leaves_other_logins_alone()
    {
        var connectionString = await LedgerQuerySeed.CreateIsolatedDatabaseAsync(fixture);
        await using var host = await StartHostAsync(connectionString);
        var (login, _) = await CreateLoginAsync(connectionString, confirm: true);
        var (bystander, _) = await CreateLoginAsync(connectionString, confirm: true);

        await using var connection = await host.ConnectAsync(login, ClientId);
        await using var bystanderConnection = await host.ConnectAsync(bystander, ClientId);

        var removed = await RunLoginAsync(connectionString, string.Empty, "remove", login.UserName);

        removed.ExitCode.Should().Be(0);
        removed.Stderr.Should().Contain("Revoked 1 grant");
        (await host.ToolsListStatusAsync(connection.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await host.ToolsListStatusAsync(bystanderConnection.AccessToken)).Should().Be(HttpStatusCode.OK);

        var listed = await RunLoginAsync(connectionString, string.Empty, "list");
        listed.Stdout.Should().NotContain(login.UserName).And.Contain(bystander.UserName);
        (await RunLoginAsync(connectionString, string.Empty, "remove", login.UserName)).ExitCode.Should().Be(1);
    }

    private async Task AssertDecisionIsSentToSignInAsync(
        McpTestHost host,
        OAuthTestDriver driver,
        DiscoveryDocuments discovery,
        ConsentPage consent,
        TestLogin login)
    {
        using var decided = await driver.PostDecisionAsync(discovery, consent, "approve");

        decided.StatusCode.Should().Be(HttpStatusCode.Redirect);
        decided.Headers.Location!.ToString().Should().Contain("/account/login").And.NotContain("code=");

        using var scope = host.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LedgerUserEntity>>();
        var user = await users.FindByNameAsync(login.UserName);
        var grants = scope.ServiceProvider.GetRequiredService<GrantRevocationService>();

        (await grants.CountActiveGrantsAsync(user!.Id)).Should().Be(0);
    }

    private static Task<McpTestHost> StartHostAsync(string connectionString) =>
        McpTestHost.StartAsync(connectionString, TestCertificates.SharedKeyRingSettings);

    private static async Task AssertPasswordIsRefusedAsync(McpTestHost host, TestLogin login)
    {
        using var browser = host.CreateBrowser();
        var driver = new OAuthTestDriver(browser);
        var discovery = await driver.DiscoverAsync();
        var (address, _, _) = OAuthTestDriver.BuildAuthorizeAddress(discovery, ClientId, OAuthTestDriver.LoopbackRedirectUri);

        using var first = await browser.GetAsync(address, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var attempt = await driver.PostPasswordAsync(first.Headers.Location!.ToString(), login.UserName, login.Password);

        attempt.StatusCode.Should().Be(HttpStatusCode.OK, "the sign-in page is shown again instead of moving on to the code page");
    }

    private async Task<(TestLogin Login, string Password)> CreateLoginAsync(string connectionString, bool confirm)
    {
        var name = NewName();
        var password = NewPassword();
        var created = await RunLoginAsync(connectionString, password, "create", name);
        created.ExitCode.Should().Be(0);
        var secret = Lines(created.Stdout)[1];

        var login = new TestLogin(Guid.Empty, name, password, secret);

        if (confirm)
        {
            var confirmedAt = DateTimeOffset.UtcNow;
            var confirmed = await RunLoginAsync(connectionString, TotpCode.Compute(secret, confirmedAt), "confirm-totp", name);
            confirmed.ExitCode.Should().Be(0);
            login.NoteCodeUsedAt(confirmedAt);
        }

        return (login, password);
    }

    private Task<CommandResult> RunLoginAsync(string connectionString, string stdin, params string[] args) =>
        RunAsync(LoginCommand.RunAsync, connectionString, stdin, args);

    private Task<CommandResult> RunGrantsAsync(string connectionString, params string[] args) =>
        RunAsync(GrantsCommand.RunAsync, connectionString, string.Empty, args);

    private static async Task<CommandResult> RunAsync(
        Func<string[], IConfiguration, Task<int>> command,
        string connectionString,
        string stdin,
        string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestCertificates.SharedKeyRingSettings)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
            .Build();

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        try
        {
            Console.SetIn(new StringReader(stdin.Length == 0 ? string.Empty : stdin + "\n"));
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var exitCode = await command(args, configuration);

            return new CommandResult(exitCode, stdoutWriter.ToString(), stderrWriter.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string NewName() => "op-" + Guid.NewGuid().ToString("N")[..10];

    private static string NewPassword() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}
