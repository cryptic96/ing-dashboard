using Ledger.Domain.Banking;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledger.Service.Ingestion;

/// <summary>Registers the provider-agnostic ingestion pipeline.</summary>
public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Binds the ingestion options and registers the sync orchestrator. A disabled provider is registered unless another
    /// provider was registered first, so the host always starts and reports a clear error when a sync is attempted.
    /// </summary>
    public static IServiceCollection AddLedgerIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBankDataProvider, DisabledBankDataProvider>();
        services.AddScoped<SyncOrchestrator>();

        return services;
    }
}
