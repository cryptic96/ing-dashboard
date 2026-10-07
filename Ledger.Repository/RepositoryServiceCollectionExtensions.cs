using Ledger.Domain.Auth;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Queries;
using Ledger.Domain.Security;
using Ledger.Repository.Health;
using Ledger.Repository.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenIddict.Core;

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
        services.AddScoped<IBankConnectionStore, BankConnectionStore>();
        services.AddScoped<IBankAuthorizationStore, BankAuthorizationStore>();
        services.AddScoped<ILedgerStore, LedgerStore>();
        services.AddScoped<ISyncRunStore, SyncRunStore>();
        services.AddScoped<IProviderCallStore, ProviderCallStore>();
        services.AddScoped<IBalanceStore, BalanceStore>();
        services.AddScoped<IIngestionStatusStore, IngestionStatusStore>();
        services.AddScoped<ILedgerQueryStore, LedgerQueryStore>();
        services.AddScoped<ITotpReplayStore, TotpReplayStore>();

        return services;
    }

    /// <summary>Persists the Data Protection key ring to <see cref="LedgerDbContext"/>.</summary>
    public static IDataProtectionBuilder PersistKeysToLedgerDatabase(this IDataProtectionBuilder builder)
    {
        return builder.PersistKeysToDbContext<LedgerDbContext>();
    }

    /// <summary>Stores the OAuth server's applications, authorizations, scopes and tokens in <see cref="LedgerDbContext"/>.</summary>
    public static OpenIddictCoreBuilder UseLedgerStores(this OpenIddictCoreBuilder builder)
    {
        builder.UseEntityFrameworkCore().UseDbContext<LedgerDbContext>();

        return builder;
    }

    /// <summary>
    /// Stores logins in <see cref="LedgerDbContext"/> through <see cref="LedgerUserStore"/>, which keeps each authenticator secret
    /// encrypted with the Data Protection key ring, and adds the default token providers, including the authenticator code provider.
    /// The host must have Data Protection registered.
    /// </summary>
    public static IdentityBuilder AddLedgerLoginStores(this IdentityBuilder builder)
    {
        return builder
            .AddEntityFrameworkStores<LedgerDbContext>()
            .AddUserStore<LedgerUserStore>()
            .AddDefaultTokenProviders();
    }

    /// <summary>Registers the "database" health check.</summary>
    public static IHealthChecksBuilder AddLedgerDatabaseCheck(this IHealthChecksBuilder builder)
    {
        return builder.AddCheck<LedgerDatabaseHealthCheck>("database");
    }
}
