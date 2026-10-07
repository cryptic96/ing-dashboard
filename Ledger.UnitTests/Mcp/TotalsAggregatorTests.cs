using FluentAssertions;
using Ledger.Domain.Queries;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies the pure roll-up behind totals: breakdown cap and remainder, ordering, and every grouping including its edges.</summary>
[Trait("Category", "Totals")]
public class TotalsAggregatorTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TotalsRow Row(string name, decimal moneyOut, decimal moneyIn, string date = "2026-08-10", string account = "acc1", string currency = "EUR", int count = 1)
    {
        return new TotalsRow(currency, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), account, name, moneyOut, moneyIn, count);
    }

    private static TotalsData Data(IReadOnlyList<TotalsRow> rows, IReadOnlyList<TotalsAccount>? accounts = null)
    {
        return new TotalsData(
            rows,
            [],
            [],
            accounts ?? [new TotalsAccount("acc1", "Joint", "EUR", Created, null)]);
    }

    private static DateRange Range(string from, string to)
    {
        return new DateRange(
            DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static CurrencyTotals Single(TotalsData data, TotalsGrouping grouping, DateRange range)
    {
        return TotalsAggregator.Aggregate(data, grouping, range).Currencies.Single();
    }

    [Fact]
    public void Thirty_counterparties_give_twenty_five_rows_and_a_remainder_that_adds_up()
    {
        var rows = Enumerable.Range(1, 30).Select(index => Row($"Example Supplier {index:00}", index, 0m)).ToList();

        var totals = Single(Data(rows), TotalsGrouping.None, Range("2026-08-01", "2026-08-31"));

        totals.Counterparties.Should().HaveCount(26);
        totals.Counterparties[25].IsRemainder.Should().BeTrue();
        totals.Counterparties[25].MergedCounterparties.Should().Be(5);
        totals.Counterparties[25].Name.Should().Contain("5");
        totals.Counterparties[25].Ref.Should().BeNull();
        totals.CounterpartyCount.Should().Be(30);
        totals.Counterparties.Sum(share => share.MoneyOut).Should().Be(totals.MoneyOut).And.Be(465m);
        totals.Counterparties.Sum(share => share.MoneyIn).Should().Be(totals.MoneyIn);
        totals.Counterparties.Sum(share => share.Count).Should().Be(totals.Count).And.Be(30);
        totals.Counterparties[0].Name.Should().Be("Example Supplier 30");
    }

    [Fact]
    public void Twenty_five_counterparties_have_no_remainder_row()
    {
        var rows = Enumerable.Range(1, 25).Select(index => Row($"Example Supplier {index:00}", index, 0m)).ToList();

        var totals = Single(Data(rows), TotalsGrouping.None, Range("2026-08-01", "2026-08-31"));

        totals.Counterparties.Should().HaveCount(25);
        totals.Counterparties.Should().NotContain(share => share.IsRemainder);
    }

    [Fact]
    public void Ties_moneyInmoney_moneyOutare_broken_by_money_moneyInand_then_by_name()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Zebra", 10m, 0m),
            Row("Example Alpha", 10m, 0m),
            Row("Example Middle", 10m, 5m),
            Row("Example Big", 50m, 0m)
        };

        var totals = Single(Data(rows), TotalsGrouping.None, Range("2026-08-01", "2026-08-31"));

        totals.Counterparties.Select(share => share.Name)
            .Should().Equal("Example Big", "Example Middle", "Example Alpha", "Example Zebra");
    }

    [Fact]
    public void Spellings_that_differ_only_moneyIncase_and_spacing_merge_and_show_the_most_frequent_one()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Market", 10m, 0m, count: 2),
            Row("EXAMPLE  market ", 5m, 0m, count: 1)
        };

        var totals = Single(Data(rows), TotalsGrouping.None, Range("2026-08-01", "2026-08-31"));

        totals.Counterparties.Should().ContainSingle();
        totals.Counterparties[0].Name.Should().Be("Example Market");
        totals.Counterparties[0].MoneyOut.Should().Be(15m);
        totals.Counterparties[0].Count.Should().Be(3);
    }

    [Fact]
    public void Rows_withmoneyOuta_counterparty_name_are_grouped_under_a_fixed_label_withmoneyOuta_reference()
    {
        var rows = new List<TotalsRow> { Row("  ", 3m, 0m), Row("Example Shop", 1m, 0m) };

        var totals = Single(Data(rows), TotalsGrouping.None, Range("2026-08-01", "2026-08-31"));

        var unnamed = totals.Counterparties.Single(share => share.Name == TotalsAggregator.NoCounterpartyLabel);
        unnamed.Ref.Should().BeNull();
        unnamed.MoneyOut.Should().Be(3m);
    }

    [Fact]
    public void Counterparty_grouping_caps_at_one_hundred_rows_plus_a_remainder_that_adds_up()
    {
        var rows = Enumerable.Range(1, 130).Select(index => Row($"Example Supplier {index:000}", index, 0m)).ToList();

        var totals = Single(Data(rows), TotalsGrouping.Counterparty, Range("2026-08-01", "2026-08-31"));

        totals.Groups.Should().HaveCount(101);
        totals.Groups[100].MergedCounterparties.Should().Be(30);
        totals.Groups[100].CounterpartyRef.Should().BeNull();
        totals.Groups[0].CounterpartyRef.Should().NotBeNull();
        totals.Groups.Sum(group => group.MoneyOut).Should().Be(totals.MoneyOut);
        totals.Groups.Sum(group => group.Count).Should().Be(totals.Count);
    }

    [Fact]
    public void Day_grouping_returns_every_day_including_empty_ones()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Bakery", 4m, 0m, "2026-10-24"),
            Row("Example Bakery", 6m, 0m, "2026-10-26")
        };

        var totals = Single(Data(rows), TotalsGrouping.Day, Range("2026-10-24", "2026-10-26"));

        totals.Groups.Select(group => group.Label).Should().Equal("2026-10-24", "2026-10-25", "2026-10-26");
        totals.Groups.Select(group => group.MoneyOut).Should().Equal(4m, 0m, 6m);
        totals.Groups[1].Count.Should().Be(0);
        totals.Groups[1].Net.Should().Be(0m);
    }

    [Fact]
    public void Day_grouping_over_ninety_two_days_is_allowed_and_ninety_three_is_refused()
    {
        var allowed = Range("2026-08-01", "2026-10-31");
        var refused = Range("2026-08-01", "2026-11-01");

        allowed.Days.Should().Be(92);
        Single(Data([]), TotalsGrouping.Day, allowed).Groups.Should().HaveCount(92);

        var act = () => TotalsAggregator.Aggregate(Data([]), TotalsGrouping.Day, refused);
        act.Should().Throw<LedgerQueryException>().WithMessage("*shorter period*");
    }

    [Fact]
    public void Week_grouping_over_seven_hundred_thirty_one_days_is_allowed_and_seven_hundred_thirty_two_is_refused()
    {
        var allowed = new DateRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1).AddDays(730));
        var refused = new DateRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1).AddDays(731));

        allowed.Days.Should().Be(731);
        Single(Data([]), TotalsGrouping.Week, allowed).Groups.Should().NotBeEmpty();

        var act = () => TotalsAggregator.Aggregate(Data([]), TotalsGrouping.Week, refused);
        act.Should().Throw<LedgerQueryException>().WithMessage("*shorter period*");
    }

    [Fact]
    public void The_caps_apply_before_any_data_is_read()
    {
        var act = () => TotalsAggregator.CheckGrouping(TotalsGrouping.Day, Range("2026-01-01", "2026-12-31"));

        act.Should().Throw<LedgerQueryException>();
        TotalsAggregator.CheckGrouping(TotalsGrouping.Month, Range("2016-01-01", "2026-12-31"));
    }

    [Fact]
    public void Week_groups_carry_iso_labels_and_dates_clipped_to_the_period()
    {
        var rows = new List<TotalsRow> { Row("Example Bakery", 5m, 0m, "2026-10-25") };

        var totals = Single(Data(rows), TotalsGrouping.Week, Range("2026-10-21", "2026-11-03"));

        totals.Groups.Select(group => group.Label).Should().Equal("2026-W43", "2026-W44", "2026-W45");
        totals.Groups[0].From.Should().Be(new DateOnly(2026, 10, 21));
        totals.Groups[0].To.Should().Be(new DateOnly(2026, 10, 25));
        totals.Groups[1].From.Should().Be(new DateOnly(2026, 10, 26));
        totals.Groups[1].To.Should().Be(new DateOnly(2026, 11, 1));
        totals.Groups[2].From.Should().Be(new DateOnly(2026, 11, 2));
        totals.Groups[2].To.Should().Be(new DateOnly(2026, 11, 3));
        totals.Groups[0].MoneyOut.Should().Be(5m);
        totals.Groups[1].MoneyOut.Should().Be(0m);
    }

    [Fact]
    public void The_last_day_of_2026_and_the_first_of_2027_fall_moneyInthe_same_iso_week()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Shop", 1m, 0m, "2026-12-31"),
            Row("Example Shop", 2m, 0m, "2027-01-01")
        };

        var totals = Single(Data(rows), TotalsGrouping.Week, Range("2026-12-31", "2027-01-01"));

        totals.Groups.Should().ContainSingle();
        totals.Groups[0].Label.Should().Be("2026-W53");
        totals.Groups[0].MoneyOut.Should().Be(3m);
        totals.Groups[0].Count.Should().Be(2);
    }

    [Fact]
    public void Month_groups_run_from_the_first_to_the_last_month_with_empty_months_included_and_dates_clipped()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Shop", 8m, 0m, "2026-08-31"),
            Row("Example Bakery", 5m, 0m, "2026-10-25")
        };

        var totals = Single(Data(rows), TotalsGrouping.Month, Range("2026-08-15", "2026-10-31"));

        totals.Groups.Select(group => group.Label).Should().Equal("2026-08", "2026-09", "2026-10");
        totals.Groups.Select(group => group.MoneyOut).Should().Equal(8m, 0m, 5m);
        totals.Groups[0].From.Should().Be(new DateOnly(2026, 8, 15));
        totals.Groups[0].To.Should().Be(new DateOnly(2026, 8, 31));
        totals.Groups[2].To.Should().Be(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void A_row_on_the_last_day_of_a_month_and_one_on_the_first_of_the_next_land_moneyIndifferent_months()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Shop", 8m, 0m, "2026-08-31"),
            Row("Example Shop", 9m, 0m, "2026-09-01")
        };

        var totals = Single(Data(rows), TotalsGrouping.Month, Range("2026-08-01", "2026-09-30"));

        totals.Groups.Select(group => group.MoneyOut).Should().Equal(8m, 9m);
    }

    [Fact]
    public void Account_grouping_returns_one_group_per_account_moneyIncreation_order_including_accounts_withmoneyOutrows()
    {
        var accounts = new List<TotalsAccount>
        {
            new("acc1", "Joint", "EUR", Created, null),
            new("acc2", "Savings", "EUR", Created.AddHours(1), null),
            new("acc3", null, "EUR", Created.AddHours(2), null)
        };
        var rows = new List<TotalsRow>
        {
            Row("Example Shop", 7m, 0m, account: "acc2"),
            Row("Example Shop", 3m, 1m, account: "acc1")
        };

        var totals = Single(Data(rows, accounts), TotalsGrouping.Account, Range("2026-08-01", "2026-08-31"));

        totals.Groups.Select(group => group.Label).Should().Equal("Joint", "Savings", "Account acc3");
        totals.Groups.Select(group => group.AccountKey).Should().Equal("acc1", "acc2", "acc3");
        totals.Groups.Select(group => group.MoneyOut).Should().Equal(3m, 7m, 0m);
        totals.Groups[2].Count.Should().Be(0);
    }

    [Fact]
    public void Groups_of_one_currency_never_include_another_currency()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Market", 10m, 0m, "2026-08-12", currency: "USD"),
            Row("Example Market", 40m, 0m, "2026-08-03")
        };

        var result = TotalsAggregator.Aggregate(Data(rows), TotalsGrouping.Day, Range("2026-08-01", "2026-08-31"));

        result.Currencies.Select(currency => currency.Currency).Should().Equal("EUR", "USD");
        result.Currencies[0].Groups.Sum(group => group.MoneyOut).Should().Be(40m);
        result.Currencies[1].Groups.Sum(group => group.MoneyOut).Should().Be(10m);
    }

    [Fact]
    public void No_grouping_returns_no_groups()
    {
        var totals = Single(Data([Row("Example Shop", 1m, 0m)]), TotalsGrouping.None, Range("2026-08-01", "2026-08-31"));

        totals.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Sums_are_exact_decimals()
    {
        var rows = new List<TotalsRow>
        {
            Row("Example Shop", 0.1m, 0m),
            Row("Example Shop", 0.2m, 0m)
        };

        var totals = Single(Data(rows), TotalsGrouping.Month, Range("2026-08-01", "2026-08-31"));

        totals.MoneyOut.Should().Be(0.3m);
        totals.Groups[0].MoneyOut.Should().Be(0.3m);
    }
}
