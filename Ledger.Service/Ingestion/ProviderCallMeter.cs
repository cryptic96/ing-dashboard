using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Counts the account-data calls of one account within one run. Every call is written to the append-only call ledger
/// before it is sent, so the ledger never undercounts what the bank saw, and a background call over the account's allowance
/// is refused before anything is recorded or sent.
/// </summary>
public class ProviderCallMeter(
    Guid accountId,
    Guid runId,
    bool background,
    IProviderCallStore callStore,
    IngestionOptions options,
    TimeProvider timeProvider) : IProviderCallMeter
{
    private readonly TimeZoneInfo _zone = options.ResolveTimeZone();

    /// <inheritdoc />
    public async ValueTask BeforeCallAsync(ProviderCallKind kind, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (background)
        {
            await EnsureBudgetAsync(now, cancellationToken);
        }

        await callStore.RecordAsync(new ProviderCallRecord(accountId, runId, now, kind, background), cancellationToken);
    }

    private async Task EnsureBudgetAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = CallBudget.WindowStart(now, options.QuotaWindow, _zone);
        var earlier = await callStore.ListBackgroundCallTimesAsync(accountId, since, cancellationToken);

        if (CallBudget.Remaining(options.BackgroundCallsPerDay, earlier, now, options.QuotaWindow, _zone) == 0)
        {
            throw new CallBudgetExhaustedException("The background call allowance for the account is used up.");
        }
    }
}
