using System.Text.RegularExpressions;
using Ledger.Repository.Entities;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Service.Cli;

/// <summary>
/// Implements `login create|confirm-totp|reset-totp|set-password|list|remove`, run before the web host starts. Passwords are read
/// from the first line of standard input and are never taken from the command line, echoed or logged. Adding a second login is
/// just another create.
/// </summary>
public static partial class LoginCommand
{
    private const string UsageMessage =
        "Usage: login create NAME | confirm-totp NAME CODE | reset-totp NAME | set-password NAME | list | remove NAME";

    private const string AuthenticatorIssuer = "Household Ledger";

    /// <summary>Runs the login subcommand. Returns 0 on success, 1 for a not-found or invalid operation, 2 for a usage error.</summary>
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        if (args is not (["create", _] or ["confirm-totp", _, _] or ["reset-totp", _] or ["set-password", _] or ["list"] or ["remove", _]))
        {
            await Console.Error.WriteLineAsync(UsageMessage);
            return 2;
        }

        if (args.Length > 1 && !NamePattern().IsMatch(args[1]))
        {
            await Console.Error.WriteLineAsync("A login name starts with a lowercase letter and holds 2 to 32 lowercase letters, digits or hyphens.");
            return 1;
        }

        using var host = OperatorHost.Build(configuration);
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LedgerUserEntity>>();
        var grants = scope.ServiceProvider.GetRequiredService<GrantRevocationService>();

        return args switch
        {
            ["create", var name] => await CreateAsync(users, name),
            ["confirm-totp", var name, var code] => await ConfirmAsync(users, name, code),
            ["reset-totp", var name] => await ResetAuthenticatorAsync(users, grants, name),
            ["set-password", var name] => await SetPasswordAsync(users, grants, name),
            ["remove", var name] => await RemoveAsync(users, grants, name),
            _ => await ListAsync(users, grants)
        };
    }

    private static async Task<int> CreateAsync(UserManager<LedgerUserEntity> users, string name)
    {
        var password = await ReadPasswordAsync();

        if (password is null)
        {
            return 1;
        }

        if (await users.FindByNameAsync(name) is not null)
        {
            await Console.Error.WriteLineAsync($"A login named '{name}' already exists.");
            return 1;
        }

        var user = new LedgerUserEntity { Id = Guid.NewGuid(), UserName = name, CreatedAt = DateTimeOffset.UtcNow };
        var created = await users.CreateAsync(user, password);

        if (!created.Succeeded)
        {
            await ReportAsync(created);
            return 1;
        }

        await PrintAuthenticatorAsync(users, user);
        await Console.Error.WriteLineAsync(
            $"Login '{name}' created. The secret above is shown once. Add it to an authenticator app, then run: login confirm-totp {name} CODE");

        return 0;
    }

    private static async Task<int> ConfirmAsync(UserManager<LedgerUserEntity> users, string name, string code)
    {
        var user = await users.FindByNameAsync(name);

        if (user is null)
        {
            return await NotFoundAsync(name);
        }

        var valid = await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code.Trim());

        if (!valid)
        {
            await Console.Error.WriteLineAsync("That code is not valid.");
            return 1;
        }

        var enabled = await users.SetTwoFactorEnabledAsync(user, true);

        if (!enabled.Succeeded)
        {
            await ReportAsync(enabled);
            return 1;
        }

        await Console.Error.WriteLineAsync($"Login '{name}' can now sign in.");

        return 0;
    }

    private static async Task<int> ResetAuthenticatorAsync(UserManager<LedgerUserEntity> users, GrantRevocationService grants, string name)
    {
        var user = await users.FindByNameAsync(name);

        if (user is null)
        {
            return await NotFoundAsync(name);
        }

        var revoked = await grants.RevokeForLoginAsync(user.Id);
        var disabled = await users.SetTwoFactorEnabledAsync(user, false);

        if (!disabled.Succeeded)
        {
            await ReportAsync(disabled);
            return 1;
        }

        await PrintAuthenticatorAsync(users, user);
        await Console.Error.WriteLineAsync(
            $"{GrantsCommand.Describe(revoked)} The secret above is shown once. Add it to an authenticator app, then run: login confirm-totp {name} CODE");

        return 0;
    }

    private static async Task<int> SetPasswordAsync(UserManager<LedgerUserEntity> users, GrantRevocationService grants, string name)
    {
        var password = await ReadPasswordAsync();

        if (password is null)
        {
            return 1;
        }

        var user = await users.FindByNameAsync(name);

        if (user is null)
        {
            return await NotFoundAsync(name);
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, password);

        if (!reset.Succeeded)
        {
            await ReportAsync(reset);
            return 1;
        }

        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        var revoked = await grants.RevokeForLoginAsync(user.Id);
        await Console.Error.WriteLineAsync($"The password of '{name}' was replaced. {GrantsCommand.Describe(revoked)}");

        return 0;
    }

    private static async Task<int> RemoveAsync(UserManager<LedgerUserEntity> users, GrantRevocationService grants, string name)
    {
        var user = await users.FindByNameAsync(name);

        if (user is null)
        {
            return await NotFoundAsync(name);
        }

        var revoked = await grants.RevokeForLoginAsync(user.Id);
        var deleted = await users.DeleteAsync(user);

        if (!deleted.Succeeded)
        {
            await ReportAsync(deleted);
            return 1;
        }

        await Console.Error.WriteLineAsync($"Login '{name}' was removed. {GrantsCommand.Describe(revoked)}");

        return 0;
    }

    private static async Task<int> ListAsync(UserManager<LedgerUserEntity> users, GrantRevocationService grants)
    {
        await Console.Out.WriteLineAsync("name\tcreated\tsecond_factor\tlocked_until\tactive_grants");

        var now = DateTimeOffset.UtcNow;

        foreach (var user in users.Users.OrderBy(login => login.UserName).ToList())
        {
            var locked = user.LockoutEnd is { } until && until > now ? until.ToString("O") : "-";
            var secondFactor = user.TwoFactorEnabled ? "yes" : "no";
            var active = await grants.CountActiveGrantsAsync(user.Id);

            await Console.Out.WriteLineAsync($"{user.UserName}\t{user.CreatedAt:O}\t{secondFactor}\t{locked}\t{active}");
        }

        return 0;
    }

    private static async Task PrintAuthenticatorAsync(UserManager<LedgerUserEntity> users, LedgerUserEntity user)
    {
        await users.ResetAuthenticatorKeyAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;

        var issuer = Uri.EscapeDataString(AuthenticatorIssuer);
        var label = $"{issuer}:{Uri.EscapeDataString(user.UserName!)}";

        await Console.Out.WriteLineAsync($"otpauth://totp/{label}?secret={key}&issuer={issuer}&digits=6&period=30");
        await Console.Out.WriteLineAsync(key);
    }

    private static async Task<string?> ReadPasswordAsync()
    {
        var line = await Console.In.ReadLineAsync();
        var password = line?.TrimEnd('\r');

        if (string.IsNullOrEmpty(password))
        {
            await Console.Error.WriteLineAsync("A password is required on standard input.");
            return null;
        }

        return password;
    }

    private static async Task<int> NotFoundAsync(string name)
    {
        await Console.Error.WriteLineAsync($"No login named '{name}' was found.");
        return 1;
    }

    private static async Task ReportAsync(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            await Console.Error.WriteLineAsync(error.Description);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex NamePattern();
}
