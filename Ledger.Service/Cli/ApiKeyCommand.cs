using Ledger.Domain.Auth;
using Ledger.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledger.Service.Cli;

/// <summary>Implements `apikey create|list|revoke`, run before the web host starts.</summary>
public static class ApiKeyCommand
{
    private const string UsageMessage = "Usage: apikey create NAME | list | revoke NAME";

    /// <summary>Runs the apikey subcommand. Returns 0 on success, 1 for a not-found or invalid operation, 2 for a usage error.</summary>
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync(UsageMessage);
            return 2;
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddLedgerRepository(hostBuilder.Configuration);

        using var host = hostBuilder.Build();
        var store = host.Services.GetRequiredService<IApiKeyStore>();

        try
        {
            return args switch
            {
                ["create", var name] => await CreateAsync(store, name),
                ["list"] => await ListAsync(store),
                ["revoke", var name] => await RevokeAsync(store, name),
                _ => await UsageErrorAsync()
            };
        }
        catch (ApiKeyOperationException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<int> CreateAsync(IApiKeyStore store, string name)
    {
        var created = await store.CreateAsync(name, CancellationToken.None);

        await Console.Out.WriteLineAsync(created.Token);
        await Console.Error.WriteLineAsync(
            $"Key '{created.Name}' created. This token is shown once and cannot be retrieved again.");

        return 0;
    }

    private static async Task<int> ListAsync(IApiKeyStore store)
    {
        var keys = await store.ListAsync(CancellationToken.None);

        await Console.Out.WriteLineAsync("name\tkey_id\tcreated\trevoked");

        foreach (var key in keys)
        {
            var revoked = key.RevokedAt is null ? "-" : key.RevokedAt.Value.ToString("O");
            await Console.Out.WriteLineAsync($"{key.Name}\t{key.KeyId}\t{key.CreatedAt:O}\t{revoked}");
        }

        return 0;
    }

    private static async Task<int> RevokeAsync(IApiKeyStore store, string name)
    {
        var revoked = await store.RevokeAsync(name, CancellationToken.None);

        if (revoked)
        {
            return 0;
        }

        await Console.Error.WriteLineAsync($"No active key named '{name}' was found.");
        return 1;
    }

    private static async Task<int> UsageErrorAsync()
    {
        await Console.Error.WriteLineAsync(UsageMessage);
        return 2;
    }
}
