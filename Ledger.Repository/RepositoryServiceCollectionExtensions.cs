using Ledger.Domain.Auth;
using Ledger.Domain.Security;
using Ledger.Repository.Health;
using Ledger.Repository.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Repository;

/// <summary>Registers the ledger database, its canary store and its health check.</summary>
public static class RepositoryServiceCollectionExtensions
{
    /// <summary>Registers <see cref="LedgerDbContext"/> against ConnectionStrings:Ledger and the canary store.</summary>
    public static IServiceCollection AddLedgerRepository(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Ledger")));

        services.AddScoped<IDataProtectionCanaryStore, DataProtectionCanaryStore>();
        services.AddScoped<IApiKeyStore, ApiKeyStore>();

        return services;
    }

    /// <summary>Persists the Data Protection key ring to <see cref="LedgerDbContext"/>.</summary>
    public static IDataProtectionBuilder PersistKeysToLedgerDatabase(this IDataProtectionBuilder builder)
    {
        return builder.PersistKeysToDbContext<LedgerDbContext>();
    }

    /// <summary>Registers the "database" health check.</summary>
    public static IHealthChecksBuilder AddLedgerDatabaseCheck(this IHealthChecksBuilder builder)
    {
        return builder.AddCheck<LedgerDatabaseHealthCheck>("database");
    }
}
