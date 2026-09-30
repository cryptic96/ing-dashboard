using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledger.Service.Ingestion;

/// <summary>Registers the provider-agnostic ingestion pipeline.</summary>
public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Binds the ingestion options, registers the sync orchestrator and the bank link flow, and registers the provider chosen by
    /// Ingestion:Provider. With None a disabled provider is registered, so the host always starts and bank linking reports that it
    /// is not configured. An unknown value, or a provider that has no adapter yet, stops startup naming only the key. The daily
    /// scheduler is always registered and idles when no provider is configured or Ingestion:SchedulerEnabled is false.
    /// </summary>
    /// <exception cref="InvalidOperationException">Ingestion:Provider holds a value that cannot be used.</exception>
    public static IServiceCollection AddLedgerIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.SectionName));
        services.Configure<BankLinkOptions>(configuration.GetSection(BankLinkOptions.SectionName));

        RegisterConfiguredProvider(services, configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBankDataProvider, DisabledBankDataProvider>();
        services.AddScoped<SyncOrchestrator>();

        services.AddSingleton<ChannelSyncDispatcher>();
        services.AddSingleton<ISyncDispatcher>(provider => provider.GetRequiredService<ChannelSyncDispatcher>());
        services.AddHostedService<SyncWorker>();
        services.AddSingleton<SyncScheduler>();
        services.AddHostedService(provider => provider.GetRequiredService<SyncScheduler>());
        services.AddScoped<BankLinkService>();

        return services;
    }

    private static void RegisterConfiguredProvider(IServiceCollection services, IConfiguration configuration)
    {
        var configured = configuration[$"{IngestionOptions.SectionName}:Provider"];

        if (string.IsNullOrWhiteSpace(configured)
            || string.Equals(configured, IngestionOptions.Providers.None, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(configured, IngestionOptions.Providers.Synthetic, StringComparison.OrdinalIgnoreCase))
        {
            var scenario = SyntheticDemoScenario.Create();
            services.AddSingleton(scenario);
            services.AddSingleton(SyntheticDemoScenario.CreateProvider(scenario));
            return;
        }

        throw new InvalidOperationException("The configuration key Ingestion:Provider does not name a usable provider.");
    }
}
