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
