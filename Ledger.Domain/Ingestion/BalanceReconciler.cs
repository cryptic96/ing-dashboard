using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>
/// A balance the ledger stored on an earlier day, as the next day's reconciliation needs it. The fetch time is when the ledger
/// read the balance from the bank, which is the reference point of a balance that carries no reference date of its own.
/// The expected amount is what the ledger expected that balance to be, when it was checked.
/// </summary>
public record BalanceSnapshotState(
    BalanceKind Kind,
    decimal Amount,
    string Currency,
    DateOnly? ReferenceDate,
    DateOnly SnapshotDate,
    DateTimeOffset? FetchedAt = null,
    decimal? ExpectedAmount = null);

/// <summary>
/// The outcome of checking a bank balance against the ledger: reconciled is null when the inputs do not allow a verdict,
/// otherwise whether the amounts agree exactly, with the amount the ledger expected and the difference from the bank's.
/// </summary>
public record BalanceCheck(bool? Reconciled, decimal? Expected, decimal? Drift);

/// <summary>
/// Decides whether the bank's balance equals the previous booked balance plus the booked transactions in between.
/// Every comparison is exact: decimals end to end, no tolerance and no rounding, so a single cent of difference is reported.
/// </summary>
public static class BalanceReconciler
{
    /// <summary>
    /// How many days before a mismatch the previous mismatch may be and still count as the same persisting drift. Snapshots are
    /// daily, so the limit tolerates a day or two without a successful sync while keeping two mismatches weeks apart from being
    /// read as persistence. The account status view applies the same limit.
    /// </summary>
    public const int MaxDaysBetweenFlaggedMismatches = 3;

    /// <summary>
    /// Returns the balance to reconcile: the first preferred kind, in the list's order, that carries a reference date. When
    /// undated balances are allowed and no dated preferred kind is present, the first preferred kind without a reference date
    /// is returned instead, to be placed on the timeline by the time the ledger fetched it.
    /// </summary>
    public static ProviderBalance? SelectReconcilable(
        IReadOnlyList<ProviderBalance> balances,
        IReadOnlyList<BalanceKind> preferred,
        bool allowUndated = false)
    {
        foreach (var kind in preferred)
        {
            var match = balances.FirstOrDefault(balance => balance.Kind == kind && balance.ReferenceDate is not null);

            if (match is not null)
            {
                return match;
            }
        }

        if (!allowUndated)
        {
            return null;
        }

        foreach (var kind in preferred)
        {
            var match = balances.FirstOrDefault(balance => balance.Kind == kind && balance.ReferenceDate is null);

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the current balance against the previous one plus the booked transactions dated after the previous reference
    /// date and up to the current one. The result is unknown when there is no previous balance, when the kinds or currencies
    /// differ, when either reference date is missing, or when the current reference date is before the previous one.
    /// </summary>
    public static BalanceCheck Check(BalanceSnapshotState? previous, ProviderBalance current, decimal bookedSumInWindow)
    {
        if (previous is null
            || previous.Kind != current.Kind
            || !string.Equals(previous.Currency, current.Currency, StringComparison.Ordinal)
            || previous.ReferenceDate is not { } previousDate
            || current.ReferenceDate is not { } currentDate
            || currentDate < previousDate)
        {
            return new BalanceCheck(null, null, null);
        }

        var expected = previous.Amount + bookedSumInWindow;
        var drift = current.Amount - expected;

        return new BalanceCheck(current.Amount == expected, expected, drift);
    }

    /// <summary>
    /// Checks a balance that carries no reference date, comparing booked amounts with booked amounts. The bank's expected
    /// balance includes the pending items it exposes, while the ledger counts a row only once it books, so the pending items
    /// live at each fetch are taken out on both sides: the bank's booked balance is its amount minus the signed sum of the
    /// pending rows live when the ledger fetched it, and the ledger's expectation is the previous booked figure plus the booked
    /// transactions the ledger first saw as booked between the two fetches. The window is supplied by the caller. The previous
    /// booked figure is the previous snapshot's own expectation when it was checked, and its bank amount minus the pending rows
    /// live at its fetch when it was only a baseline. A pending item the bank exposes therefore never causes drift however long
    /// it stays pending, counts exactly once when it books, and leaves no trace when it is dropped. Carrying the ledger's
    /// expectation forward is what lets a reservation the bank deducts without exposing it as a pending item show up once and
    /// then resolve when it books, while a transaction the ledger never received keeps showing as drift. The recorded expected
    /// amount is the booked-only expectation and the drift is the bank's booked balance minus it. The result is unknown when
    /// there is no previous balance, when the kinds or currencies differ, or when the previous balance was dated or has no
    /// recorded fetch time, because then the two do not describe the same timeline. Every comparison is exact.
    /// </summary>
    public static BalanceCheck CheckUndated(
        BalanceSnapshotState? previous,
        ProviderBalance current,
        decimal bookedSumSincePrevious,
        decimal pendingSumAtPrevious = 0m,
        decimal pendingSumAtCurrent = 0m)
    {
        if (previous is null
            || previous.Kind != current.Kind
            || !string.Equals(previous.Currency, current.Currency, StringComparison.Ordinal)
            || previous.ReferenceDate is not null
            || previous.FetchedAt is null
            || current.ReferenceDate is not null)
        {
            return new BalanceCheck(null, null, null);
        }

        var start = previous.ExpectedAmount ?? previous.Amount - pendingSumAtPrevious;
        var expected = start + bookedSumSincePrevious;
        var drift = current.Amount - pendingSumAtCurrent - expected;

        return new BalanceCheck(drift == 0m, expected, drift);
    }
}
