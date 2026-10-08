using Ledger.Repository;
using Ledger.Service.OAuth;
using Ledger.Service.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledger.Service.Cli;

/// <summary>Builds the small host the operator commands run in: the database, the logins and the OAuth stores, and nothing that listens.</summary>
internal static class OperatorHost
{
    /// <summary>
    /// Builds a host with logging switched off. Data protection uses the same database key ring and certificate as the web host,
    /// so an authenticator secret written by a command is readable by the web host and the other way round; nothing is written
    /// to disk.
    /// </summary>
    public static IHost Build(IConfiguration configuration)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddLedgerRepository(hostBuilder.Configuration);
        hostBuilder.Services.AddLedgerDataProtection(hostBuilder.Configuration, hostBuilder.Environment);
        hostBuilder.Services.AddLedgerOAuthCore(hostBuilder.Configuration);

        return hostBuilder.Build();
    }
}
