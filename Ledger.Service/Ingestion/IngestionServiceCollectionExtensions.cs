using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.EnableBanking;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Ingestion;

/// <summary>Registers the provider-agnostic ingestion pipeline.</summary>
public static class IngestionServiceCollectionExtensions
{
    private const long MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Binds the ingestion options, registers the sync orchestrator and the bank link flow, and registers the provider chosen by
    /// Ingestion:Provider. With None a disabled provider is registered, so the host always starts and bank linking reports that it
    /// is not configured. An unknown value stops startup naming only the key. With EnableBanking the aggregator client is registered with the
    /// account-information guard as its only outbound handler; its credentials are read when first used. The daily
    /// scheduler is always registered and idles when no provider is configured or Ingestion:SchedulerEnabled is false. The metrics
    /// refresher is always registered, because consent and sync state are worth exposing whatever the provider.
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

        services.AddSingleton<OrphanedRunRecovery>();
        services.AddHostedService(provider => provider.GetRequiredService<OrphanedRunRecovery>());
        services.AddSingleton<ChannelSyncDispatcher>();
        services.AddSingleton<ISyncDispatcher>(provider => provider.GetRequiredService<ChannelSyncDispatcher>());
        services.AddHostedService<SyncWorker>();
        services.AddSingleton<SyncScheduler>();
        services.AddHostedService(provider => provider.GetRequiredService<SyncScheduler>());
        services.AddScoped<BankLinkService>();

        services.AddSingleton<SyncMetricsRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<SyncMetricsRefresher>());

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

        if (string.Equals(configured, IngestionOptions.Providers.EnableBanking, StringComparison.OrdinalIgnoreCase))
        {
            RegisterEnableBanking(services, configuration);
            return;
        }

        throw new InvalidOperationException("The configuration key Ingestion:Provider does not name a usable provider.");
    }

    private static void RegisterEnableBanking(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EnableBankingOptions>(configuration.GetSection(EnableBankingOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EnableBankingTokenMinter>();
        services.AddSingleton<AspspRequirementsCache>();
        services.AddTransient<AisOnlyGuardHandler>();

        services.AddHttpClient<EnableBankingClient>((provider, client) =>
            {
                var settings = provider.GetRequiredService<IOptions<EnableBankingOptions>>().Value;
                client.BaseAddress = EnableBankingOptions.BaseAddress;
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds));
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<AisOnlyGuardHandler>()
            .RemoveAllLoggers();

        services.AddTransient<IBankDataProvider>(provider => provider.GetRequiredService<EnableBankingClient>());
    }
}
