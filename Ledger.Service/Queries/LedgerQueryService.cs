using System.Globalization;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Queries;
using Ledger.Service.Ingestion;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Queries;

/// <summary>
/// Answers the questions Claude asks of the ledger. It reads through the domain stores and shapes the result for a tool; it
/// never touches the database context and never lets a bank identifier into a result.
/// </summary>
public class LedgerQueryService(
    ILedgerQueryStore store,
    IIngestionStatusStore ingestionStatus,
    IOptions<IngestionOptions> ingestionOptions,
    TimeProvider timeProvider)
{
    private const string NothingSyncedNote = "No accounts are synced yet.";

    /// <summary>
    /// Lists every synced account in creation order with its name, latest balance and whether it reconciles, the last successful
    /// sync, how far booked history reaches and how many items are pending, plus today's date in the configured time zone.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<OverviewResult> OverviewAsync(CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var zone = options.ResolveTimeZone();
        var now = timeProvider.GetUtcNow();
        var today = SyncSchedule.LocalDate(now, zone);

        var accounts = await store.ReadOverviewAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return new OverviewResult(DateText(today), options.TimeZone, [], NothingSyncedNote);
        }

        var status = await ingestionStatus.ReadAsync(now, cancellationToken);
        var healthByKey = status.Accounts.ToDictionary(account => account.AccountKey, StringComparer.Ordinal);

        var shown = accounts
            .Select(account =>
            {
                var health = healthByKey.GetValueOrDefault(account.AccountKey);

                return new OverviewAccount(
                    account.DisplayName ?? "Account " + account.AccountKey,
                    account.AccountKey,
                    account.Kind,
                    account.Currency,
                    health?.LastSuccessAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    account.EarliestBookedDate is { } from ? DateText(from) : null,
                    account.LatestBookedDate is { } to ? DateText(to) : null,
                    account.PendingCount,
                    account.LatestBalance is { } balance
                        ? new OverviewBalance(MoneyText.Format(balance.Amount), balance.Currency, balance.Kind, DateText(balance.AppliesTo))
                        : null,
                    health?.LatestReconciled switch
                    {
                        true => "yes",
                        false => "no",
                        null => "unknown"
                    });
            })
            .ToList();

        return new OverviewResult(DateText(today), options.TimeZone, shown, null);
    }

    private static string DateText(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
