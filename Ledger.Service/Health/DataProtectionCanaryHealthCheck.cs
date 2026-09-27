using System.Security.Cryptography;
using System.Text;
using Ledger.Domain.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Service.Health;

/// <summary>Reports Healthy only when the canary row exists and still decrypts to its recorded hash. Never writes.</summary>
public class DataProtectionCanaryHealthCheck(
    IDataProtectionCanaryStore store,
    ISecretProtector protector) : IHealthCheck
{
    private const string UnhealthyDescription = "The Data Protection canary is missing or cannot be decrypted.";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canary = await store.GetAsync(cancellationToken);
        if (canary is null)
        {
            return HealthCheckResult.Unhealthy(UnhealthyDescription);
        }

        try
        {
            var plaintext = protector.Unprotect(canary.ProtectedPayload);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

            return CryptographicOperations.FixedTimeEquals(hash, canary.PlaintextSha256)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(UnhealthyDescription);
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy(UnhealthyDescription);
        }
    }
}
