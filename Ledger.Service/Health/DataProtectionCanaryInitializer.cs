using System.Security.Cryptography;
using System.Text;
using Ledger.Domain.Security;

namespace Ledger.Service.Health;

/// <summary>Creates the canary row once, on first start, and never touches it again.</summary>
public class DataProtectionCanaryInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DataProtectionCanaryInitializer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IDataProtectionCanaryStore>();
                var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

                var existing = await store.GetAsync(stoppingToken);
                if (existing is not null)
                {
                    return;
                }

                var plaintextBytes = RandomNumberGenerator.GetBytes(32);
                var plaintext = Convert.ToBase64String(plaintextBytes)
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .TrimEnd('=');
                var protectedPayload = protector.Protect(plaintext);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

                await store.TryCreateAsync(
                    new CanaryRecord(protectedPayload, hash, DateTimeOffset.UtcNow),
                    stoppingToken);

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not initialize the Data Protection canary; retrying.");
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
