using Ledger.Repository;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledger.Service.Cli;

/// <summary>Builds the small host the operator commands run in: the database, the logins and the OAuth stores, and nothing that listens.</summary>
internal static class OperatorHost
{
    /// <summary>
    /// Builds a host with logging switched off. Data protection uses a key ring that lives only as long as the command, because
    /// the commands never keep anything that needs one beyond their own run.
    /// </summary>
    public static IHost Build(IConfiguration configuration)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddLedgerRepository(hostBuilder.Configuration);
        hostBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        hostBuilder.Services.AddLedgerOAuthCore(hostBuilder.Configuration);

        return hostBuilder.Build();
    }
}
