using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Repository.Health;

/// <summary>Confirms the ledger database is reachable over its connection.</summary>
public class LedgerDatabaseHealthCheck(LedgerDbContext dbContext) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

        return canConnect
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The ledger database is unreachable.");
    }
}
