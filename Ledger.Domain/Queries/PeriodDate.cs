using Ledger.Domain.Ingestion;

namespace Ledger.Domain.Queries;

/// <summary>Decides which calendar day a transaction belongs to when totals are cut into periods.</summary>
public static class PeriodDate
{
    /// <summary>
    /// Returns the day the transaction counts on. A booked transaction uses its booking date, else its value date, else its
    /// transaction date, else the zone's calendar day on which the ledger first saw it. A pending transaction uses its booking
    /// date, else the zone's calendar day on which the ledger first saw it. Bank dates are calendar dates and are used as they
    /// are; only the first-seen instant is converted, and it is converted into the zone, never through UTC. This is deliberately
    /// not the reconciler's effective date, which prefers the transaction date, and not the reporting views' date: a total must
    /// agree with the date the bank booked a payment on, because that is the date a statement shows.
    /// </summary>
    /// <param name="status">Whether the transaction is booked or pending.</param>
    /// <param name="bookingDate">The bank's booking date, if any.</param>
    /// <param name="valueDate">The bank's value date, if any.</param>
    /// <param name="transactionDate">The bank's transaction date, if any.</param>
    /// <param name="firstSeenAt">The instant the ledger first saw the transaction.</param>
    /// <param name="zone">The zone whose calendar decides the fallback day.</param>
    public static DateOnly Of(
        LedgerTransactionStatus status,
        DateOnly? bookingDate,
        DateOnly? valueDate,
        DateOnly? transactionDate,
        DateTimeOffset firstSeenAt,
        TimeZoneInfo zone)
    {
        var firstSeenDay = SyncSchedule.LocalDate(firstSeenAt, zone);

        return status == LedgerTransactionStatus.Booked
            ? bookingDate ?? valueDate ?? transactionDate ?? firstSeenDay
            : bookingDate ?? firstSeenDay;
    }
}
