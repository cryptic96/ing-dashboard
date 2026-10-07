using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>
/// Counts the account-data calls of one account within one run. Every call is written to the append-only call ledger
/// before it is sent, so the ledger never undercounts what the bank saw. A background call is recorded as such, and when the
/// allowance is enforced one over the account's allowance is refused before anything is recorded or sent.
/// </summary>
/// <param name="accountId">The account the calls are made for.</param>
/// <param name="runId">The sync run the calls belong to.</param>
/// <param name="background">Whether the calls are background calls, which is how they are recorded.</param>
/// <param name="enforceBudget">
/// Whether a background call is refused once the allowance is used up. It is false for the sync that follows a link or a
/// renewal: that fetch is the only chance to read the full history, so it is counted but never cut short.
/// </param>
/// <param name="callStore">Where every call is recorded.</param>
/// <param name="options">The allowance and the window it is measured in.</param>
/// <param name="timeProvider">The clock.</param>
public class ProviderCallMeter(
    Guid accountId,
    Guid runId,
    bool background,
    bool enforceBudget,
    IProviderCallStore callStore,
    IngestionOptions options,
    TimeProvider timeProvider) : IProviderCallMeter
{
    private readonly TimeZoneInfo _zone = options.ResolveTimeZone();

    /// <inheritdoc />
    public async ValueTask BeforeCallAsync(ProviderCallKind kind, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (background && enforceBudget)
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
