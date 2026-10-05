using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>
/// A balance the ledger stored on an earlier day, as the next day's reconciliation needs it. The fetch time is when the ledger
/// read the balance from the bank, which is the reference point of a balance that carries no reference date of its own.
/// </summary>
public record BalanceSnapshotState(
    BalanceKind Kind,
    decimal Amount,
    string Currency,
    DateOnly? ReferenceDate,
    DateOnly SnapshotDate,
    DateTimeOffset? FetchedAt = null);

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
    /// Checks a balance that carries no reference date against the previous balance of the same kind plus the booked
    /// transactions the ledger first saw as booked between the two fetches. The window is supplied by the caller. The result is
    /// unknown when there is no previous balance, when the kinds or currencies differ, or when the previous balance was dated
    /// or has no recorded fetch time, because then the two do not describe the same timeline.
    /// </summary>
    public static BalanceCheck CheckUndated(BalanceSnapshotState? previous, ProviderBalance current, decimal bookedSumSincePrevious)
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

        var expected = previous.Amount + bookedSumSincePrevious;
        var drift = current.Amount - expected;

        return new BalanceCheck(current.Amount == expected, expected, drift);
    }
}
