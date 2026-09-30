using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Counts the account-data calls of one account within one run. Every call is written to the append-only call ledger
/// before it is sent, so the ledger never undercounts what the bank saw.
/// </summary>
public class ProviderCallMeter(
    Guid accountId,
    Guid runId,
    bool background,
    IProviderCallStore callStore,
    TimeProvider timeProvider) : IProviderCallMeter
{
    /// <inheritdoc />
    public async ValueTask BeforeCallAsync(ProviderCallKind kind, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await callStore.RecordAsync(new ProviderCallRecord(accountId, runId, now, kind, background), cancellationToken);
    }
}
