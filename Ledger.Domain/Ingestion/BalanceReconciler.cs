using Ledger.Domain.Banking;

namespace Ledger.Domain.Ingestion;

/// <summary>A balance the ledger stored on an earlier day, as the next day's reconciliation needs it.</summary>
public record BalanceSnapshotState(
    BalanceKind Kind,
    decimal Amount,
    string Currency,
    DateOnly? ReferenceDate,
    DateOnly SnapshotDate);

/// <summary>
/// The outcome of checking a bank balance against the ledger: reconciled is null when the inputs do not allow a verdict,
/// otherwise whether the amounts agree exactly, with the amount the ledger expected and the difference from the bank's.
/// </summary>
public record BalanceCheck(bool? Reconciled, decimal? Expected, decimal? Drift);

/// <summary>
/// Decides whether the bank's booked balance equals the previous booked balance plus the booked transactions in between.
/// Every comparison is exact: decimals end to end, no tolerance and no rounding, so a single cent of difference is reported.
/// </summary>
public static class BalanceReconciler
{
    /// <summary>
    /// Returns the first balance whose kind appears in the preferred list, in the list's order, and that carries a reference
    /// date. A preferred kind without a reference date is skipped because it cannot be placed on the booking timeline.
    /// </summary>
    public static ProviderBalance? SelectReconcilable(
        IReadOnlyList<ProviderBalance> balances,
        IReadOnlyList<BalanceKind> preferred)
    {
        foreach (var kind in preferred)
        {
            var match = balances.FirstOrDefault(balance => balance.Kind == kind && balance.ReferenceDate is not null);

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
}
