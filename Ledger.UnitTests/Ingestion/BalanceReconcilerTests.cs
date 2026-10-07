using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the exact, tolerance-free balance reconciliation and the choice of which balance to reconcile.</summary>
[Trait("Category", "Balances")]
public class BalanceReconcilerTests
{
    private static readonly DateOnly DayOne = new(2026, 10, 20);
    private static readonly DateOnly DayTwo = new(2026, 10, 21);

    private static readonly BalanceKind[] Preferred = [BalanceKind.ClosingBooked, BalanceKind.InterimBooked];
    private static readonly BalanceKind[] PreferredWithExpected = [BalanceKind.ClosingBooked, BalanceKind.InterimBooked, BalanceKind.Expected];
    private static readonly DateTimeOffset FetchedDayOne = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_booked_balance_equal_to_the_previous_balance_plus_the_booked_transactions_reconciles()
    {
        var previous = Previous(1000.00m, DayOne);
        var current = Current(1150.11m, DayTwo);

        var check = BalanceReconciler.Check(previous, current, 250.10m - 99.99m);

        check.Reconciled.Should().BeTrue();
        check.Expected.Should().Be(1150.11m);
        check.Drift.Should().Be(0m);
    }

    [Fact]
    public void One_cent_too_much_is_reported_as_drift_and_never_tolerated()
    {
        var check = BalanceReconciler.Check(Previous(1000.00m, DayOne), Current(1150.12m, DayTwo), 250.10m - 99.99m);

        check.Reconciled.Should().BeFalse();
        check.Expected.Should().Be(1150.11m);
        check.Drift.Should().Be(0.01m);
    }

    [Fact]
    public void One_cent_too_little_is_reported_as_negative_drift()
    {
        var check = BalanceReconciler.Check(Previous(1000.00m, DayOne), Current(1150.10m, DayTwo), 250.10m - 99.99m);

        check.Reconciled.Should().BeFalse();
        check.Drift.Should().Be(-0.01m);
    }

    [Fact]
    public void Amounts_with_different_scales_but_the_same_value_reconcile()
    {
        var check = BalanceReconciler.Check(Previous(10.5m, DayOne), Current(10.5000m, DayTwo), 0.00m);

        check.Reconciled.Should().BeTrue();
    }

    [Fact]
    public void Without_a_previous_snapshot_the_result_is_unknown()
    {
        BalanceReconciler.Check(null, Current(10m, DayTwo), 0m).Should().Be(new BalanceCheck(null, null, null));
    }

    [Fact]
    public void A_missing_reference_date_on_either_side_gives_an_unknown_result()
    {
        BalanceReconciler.Check(Previous(10m, null), Current(10m, DayTwo), 0m).Reconciled.Should().BeNull();
        BalanceReconciler.Check(Previous(10m, DayOne), Current(10m, null), 0m).Reconciled.Should().BeNull();
    }

    [Fact]
    public void Differing_currencies_give_an_unknown_result()
    {
        var previous = Previous(10m, DayOne) with { Currency = "USD" };

        BalanceReconciler.Check(previous, Current(10m, DayTwo), 0m).Should().Be(new BalanceCheck(null, null, null));
    }

    [Fact]
    public void Differing_kinds_give_an_unknown_result()
    {
        var previous = Previous(10m, DayOne) with { Kind = BalanceKind.InterimBooked };

        BalanceReconciler.Check(previous, Current(10m, DayTwo), 0m).Reconciled.Should().BeNull();
    }

    [Fact]
    public void A_new_reference_date_before_the_previous_one_gives_an_unknown_result()
    {
        BalanceReconciler.Check(Previous(10m, DayTwo), Current(10m, DayOne), 0m).Reconciled.Should().BeNull();
    }

    [Fact]
    public void The_same_reference_date_on_both_days_reconciles_only_when_the_amounts_are_equal()
    {
        BalanceReconciler.Check(Previous(10m, DayOne), Current(10m, DayOne), 0m).Reconciled.Should().BeTrue();

        var different = BalanceReconciler.Check(Previous(10m, DayOne), Current(10.01m, DayOne), 0m);

        different.Reconciled.Should().BeFalse();
        different.Drift.Should().Be(0.01m);
    }

    [Fact]
    public void Selection_prefers_closing_booked_over_interim_booked()
    {
        var balances = new[]
        {
            Balance(BalanceKind.InterimBooked, 1m, DayTwo),
            Balance(BalanceKind.ClosingBooked, 2m, DayTwo)
        };

        BalanceReconciler.SelectReconcilable(balances, Preferred)!.Kind.Should().Be(BalanceKind.ClosingBooked);
    }

    [Fact]
    public void Selection_skips_a_preferred_kind_without_a_reference_date()
    {
        var balances = new[]
        {
            Balance(BalanceKind.ClosingBooked, 2m, null),
            Balance(BalanceKind.InterimBooked, 1m, DayTwo)
        };

        BalanceReconciler.SelectReconcilable(balances, Preferred)!.Kind.Should().Be(BalanceKind.InterimBooked);
    }

    [Fact]
    public void Selection_returns_nothing_when_no_preferred_kind_is_present()
    {
        var balances = new[]
        {
            Balance(BalanceKind.InterimAvailable, 1m, DayTwo),
            Balance(BalanceKind.Expected, 2m, DayTwo)
        };

        BalanceReconciler.SelectReconcilable(balances, Preferred).Should().BeNull();
    }

    [Fact]
    public void With_undated_balances_allowed_an_expected_balance_without_a_reference_date_is_selected()
    {
        var balances = new[] { Balance(BalanceKind.Expected, 2m, null), Balance(BalanceKind.InterimAvailable, 1m, null) };

        BalanceReconciler.SelectReconcilable(balances, PreferredWithExpected, allowUndated: true)!.Kind.Should().Be(BalanceKind.Expected);
    }

    [Fact]
    public void With_undated_balances_not_allowed_an_expected_balance_without_a_reference_date_is_not_selected()
    {
        var balances = new[] { Balance(BalanceKind.Expected, 2m, null) };

        BalanceReconciler.SelectReconcilable(balances, PreferredWithExpected).Should().BeNull();
        BalanceReconciler.SelectReconcilable(balances, PreferredWithExpected, allowUndated: false).Should().BeNull();
    }

    [Fact]
    public void A_dated_booked_balance_still_wins_over_an_undated_one_when_undated_balances_are_allowed()
    {
        var balances = new[]
        {
            Balance(BalanceKind.Expected, 2m, null),
            Balance(BalanceKind.InterimBooked, 1m, DayTwo)
        };

        BalanceReconciler.SelectReconcilable(balances, PreferredWithExpected, allowUndated: true)!.Kind.Should().Be(BalanceKind.InterimBooked);
    }

    [Fact]
    public void Undated_balances_reconcile_exactly_on_the_sum_since_the_previous_fetch()
    {
        var previous = UndatedPrevious(1000.00m);

        var matching = BalanceReconciler.CheckUndated(previous, Undated(1150.11m), 250.10m - 99.99m);

        matching.Reconciled.Should().BeTrue();
        matching.Expected.Should().Be(1150.11m);
        matching.Drift.Should().Be(0m);

        var off = BalanceReconciler.CheckUndated(previous, Undated(1150.12m), 250.10m - 99.99m);

        off.Reconciled.Should().BeFalse();
        off.Drift.Should().Be(0.01m);
    }

    [Fact]
    public void An_undated_check_without_a_comparable_previous_balance_is_unknown()
    {
        var unknown = new BalanceCheck(null, null, null);

        BalanceReconciler.CheckUndated(null, Undated(10m), 0m).Should().Be(unknown);
        BalanceReconciler.CheckUndated(UndatedPrevious(10m) with { FetchedAt = null }, Undated(10m), 0m).Should().Be(unknown);
        BalanceReconciler.CheckUndated(UndatedPrevious(10m) with { ReferenceDate = DayOne }, Undated(10m), 0m).Should().Be(unknown);
        BalanceReconciler.CheckUndated(UndatedPrevious(10m) with { Currency = "USD" }, Undated(10m), 0m).Should().Be(unknown);
        BalanceReconciler.CheckUndated(UndatedPrevious(10m), Undated(10m) with { ReferenceDate = DayTwo }, 0m).Should().Be(unknown);
    }

    [Fact]
    public void An_undated_check_carries_the_ledger_expectation_forward_so_a_gap_persists_and_a_booked_reservation_resolves()
    {
        var checkedPrevious = UndatedPrevious(950.00m) with { ExpectedAmount = 1000.00m };

        var persistentGap = BalanceReconciler.CheckUndated(checkedPrevious, Undated(950.00m), 0m);

        persistentGap.Reconciled.Should().BeFalse();
        persistentGap.Expected.Should().Be(1000.00m);
        persistentGap.Drift.Should().Be(-50.00m);

        var bookedReservation = BalanceReconciler.CheckUndated(checkedPrevious, Undated(950.00m), -50.00m);

        bookedReservation.Reconciled.Should().BeTrue();
        bookedReservation.Drift.Should().Be(0m);
    }

    [Fact]
    public void A_baseline_that_still_contained_a_pending_item_reconciles_once_that_item_has_booked()
    {
        var baseline = UndatedPrevious(990.00m);

        var bookedNextDay = BalanceReconciler.CheckUndated(baseline, Undated(990.00m), -10.00m, pendingSumAtPrevious: -10.00m);

        bookedNextDay.Reconciled.Should().BeTrue();
        bookedNextDay.Expected.Should().Be(990.00m);
        bookedNextDay.Drift.Should().Be(0m);

        var droppedNextDay = BalanceReconciler.CheckUndated(baseline, Undated(1000.00m), 0m, pendingSumAtPrevious: -10.00m);

        droppedNextDay.Reconciled.Should().BeTrue();
        droppedNextDay.Expected.Should().Be(1000.00m);
    }

    [Fact]
    public void A_baseline_with_a_pending_item_that_has_not_booked_yet_shows_the_drift_instead_of_hiding_it()
    {
        var baseline = UndatedPrevious(990.00m);

        var stillPending = BalanceReconciler.CheckUndated(baseline, Undated(990.00m), 0m, pendingSumAtPrevious: -10.00m);

        stillPending.Reconciled.Should().BeFalse();
        stillPending.Expected.Should().Be(1000.00m);
        stillPending.Drift.Should().Be(-10.00m);
    }

    [Fact]
    public void The_pending_sum_at_the_previous_fetch_is_ignored_once_the_previous_snapshot_carries_its_own_expectation()
    {
        var checkedPrevious = UndatedPrevious(950.00m) with { ExpectedAmount = 1000.00m };

        var result = BalanceReconciler.CheckUndated(checkedPrevious, Undated(950.00m), -50.00m, pendingSumAtPrevious: -999.00m);

        result.Reconciled.Should().BeTrue();
        result.Expected.Should().Be(950.00m);
    }

    private static BalanceSnapshotState UndatedPrevious(decimal amount)
    {
        return new BalanceSnapshotState(BalanceKind.Expected, amount, "EUR", null, DayOne, FetchedDayOne);
    }

    private static ProviderBalance Undated(decimal amount)
    {
        return Balance(BalanceKind.Expected, amount, null);
    }

    private static BalanceSnapshotState Previous(decimal amount, DateOnly? referenceDate)
    {
        return new BalanceSnapshotState(BalanceKind.ClosingBooked, amount, "EUR", referenceDate, DayOne);
    }

    private static ProviderBalance Current(decimal amount, DateOnly? referenceDate)
    {
        return Balance(BalanceKind.ClosingBooked, amount, referenceDate);
    }

    private static ProviderBalance Balance(BalanceKind kind, decimal amount, DateOnly? referenceDate)
    {
        return new ProviderBalance(kind, "SYNT", amount, "EUR", referenceDate);
    }
}
