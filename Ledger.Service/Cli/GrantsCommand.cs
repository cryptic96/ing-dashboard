using System.Text.RegularExpressions;
using Ledger.Service.OAuth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Service.Cli;

/// <summary>Implements `grants list|revoke-all|revoke`, run before the web host starts. It never prints a token.</summary>
public static partial class GrantsCommand
{
    private const string UsageMessage = "Usage: grants list | revoke-all | revoke GRANT_ID";

    /// <summary>Runs the grants subcommand. Returns 0 on success, 1 for an unknown grant, 2 for a usage error.</summary>
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        if (args is not (["list"] or ["revoke-all"] or ["revoke", _]))
        {
            await Console.Error.WriteLineAsync(UsageMessage);
            return 2;
        }

        if (args is ["revoke", var requested] && !GrantIdPattern().IsMatch(requested))
        {
            await Console.Error.WriteLineAsync("Invalid grant id.");
            return 1;
        }

        using var host = OperatorHost.Build(configuration);
        using var scope = host.Services.CreateScope();
        var grants = scope.ServiceProvider.GetRequiredService<GrantRevocationService>();

        return args switch
        {
            ["list"] => await ListAsync(grants),
            ["revoke-all"] => await RevokeAllAsync(grants),
            _ => await RevokeAsync(grants, args[1])
        };
    }

    internal static string Describe(RevocationResult result)
    {
        var grants = result.Grants == 1 ? "1 grant" : $"{result.Grants} grants";
        var tokens = result.Tokens == 1 ? "1 token" : $"{result.Tokens} tokens";

        return $"Revoked {grants} and {tokens}.";
    }

    private static async Task<int> ListAsync(GrantRevocationService grants)
    {
        await Console.Out.WriteLineAsync("grant_id\tclient\tlogin\tcreated\tstatus\tlive_tokens");

        foreach (var grant in await grants.ListAsync())
        {
            var created = grant.CreatedAt is null ? "-" : grant.CreatedAt.Value.ToString("O");
            await Console.Out.WriteLineAsync(
                $"{grant.GrantId}\t{grant.ClientId}\t{grant.LoginName ?? "-"}\t{created}\t{grant.Status}\t{grant.LiveTokens}");
        }

        return 0;
    }

    private static async Task<int> RevokeAllAsync(GrantRevocationService grants)
    {
        var result = await grants.RevokeAllAsync();
        await Console.Error.WriteLineAsync(Describe(result));

        return 0;
    }

    private static async Task<int> RevokeAsync(GrantRevocationService grants, string grantId)
    {
        var result = await grants.RevokeAsync(grantId);

        if (result.Grants == 0 && result.Tokens == 0)
        {
            await Console.Error.WriteLineAsync("No valid grant with that id was found.");
            return 1;
        }

        await Console.Error.WriteLineAsync(Describe(result));

        return 0;
    }

    [GeneratedRegex("^[0-9a-f-]{36}$")]
    private static partial Regex GrantIdPattern();
}
