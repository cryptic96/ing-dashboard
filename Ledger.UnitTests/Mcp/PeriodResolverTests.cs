using FluentAssertions;
using Ledger.Domain.Ingestion;
using Ledger.Domain.Queries;
using Microsoft.Extensions.Time.Testing;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies periods resolve to Amsterdam calendar days, on both sides of midnight and of every clock change, and that bad requests are refused.</summary>
[Trait("Category", "Periods")]
public class PeriodResolverTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static PeriodResolver At(string utcInstant)
    {
        return new PeriodResolver(new FakeTimeProvider(DateTimeOffset.Parse(utcInstant)), Amsterdam);
    }

    private static DateOnly Day(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-09-30T22:05:00Z", "2026-10-01")]
    [InlineData("2026-08-31T21:30:00Z", "2026-08-31")]
    [InlineData("2026-08-31T22:00:00Z", "2026-09-01")]
    [InlineData("2026-03-29T00:30:00Z", "2026-03-29")]
    [InlineData("2026-03-29T01:30:00Z", "2026-03-29")]
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25")]
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25")]
    [InlineData("2027-03-28T00:30:00Z", "2027-03-28")]
    [InlineData("2027-03-28T01:30:00Z", "2027-03-28")]
    [InlineData("2027-10-31T00:30:00Z", "2027-10-31")]
    [InlineData("2027-10-31T01:30:00Z", "2027-10-31")]
    public void Today_is_the_calendar_day_in_the_configured_zone_never_the_utc_day(string utcInstant, string expected)
    {
        At(utcInstant).Today().Should().Be(Day(expected));
    }

    [Fact]
    public void Day_and_month_keywords_resolve_just_after_amsterdam_midnight_while_utc_is_still_the_previous_day()
    {
        var resolver = At("2026-09-30T22:05:00Z");

        resolver.Resolve("yesterday", null, null).Range.Should().Be(new DateRange(Day("2026-09-30"), Day("2026-09-30")));
        resolver.Resolve("this_month", null, null).Range.Should().Be(new DateRange(Day("2026-10-01"), Day("2026-10-31")));
        resolver.Resolve("last_month", null, null).Range.Should().Be(new DateRange(Day("2026-09-01"), Day("2026-09-30")));
    }

    [Fact]
    public void Year_keywords_follow_the_amsterdam_new_year()
    {
        var resolver = At("2026-12-31T23:05:00Z");

        resolver.Resolve("this_year", null, null).Range.Should().Be(new DateRange(Day("2027-01-01"), Day("2027-12-31")));
        resolver.Resolve("last_year", null, null).Range.Should().Be(new DateRange(Day("2026-01-01"), Day("2026-12-31")));
    }

    [Fact]
    public void Weeks_run_from_monday_to_sunday()
    {
        var resolver = At("2026-10-07T10:00:00Z");

        resolver.Resolve("this_week", null, null).Range.Should().Be(new DateRange(Day("2026-10-05"), Day("2026-10-11")));
        resolver.Resolve("last_week", null, null).Range.Should().Be(new DateRange(Day("2026-09-28"), Day("2026-10-04")));
    }

    [Fact]
    public void A_sunday_belongs_to_the_week_that_started_on_the_previous_monday()
    {
        var resolver = At("2026-10-11T10:00:00Z");

        resolver.Resolve("this_week", null, null).Range.Should().Be(new DateRange(Day("2026-10-05"), Day("2026-10-11")));
    }

    [Theory]
    [InlineData("2026-10-26T10:00:00Z", 30)]
    [InlineData("2026-03-30T10:00:00Z", 30)]
    public void Last_thirty_days_covers_exactly_thirty_calendar_days_across_a_clock_change(string utcInstant, int expectedDays)
    {
        var range = At(utcInstant).Resolve("last_30_days", null, null).Range;

        range.Days.Should().Be(expectedDays);
        range.To.Should().Be(At(utcInstant).Today());
    }

    [Fact]
    public void Last_ninety_days_covers_exactly_ninety_calendar_days()
    {
        At("2026-10-07T10:00:00Z").Resolve("last_90_days", null, null).Range.Days.Should().Be(90);
    }

    [Fact]
    public void Resolved_period_reports_the_keyword_that_was_asked_for()
    {
        var resolved = At("2026-10-07T10:00:00Z").Resolve(" This_Month ", null, null);

        resolved.Requested.Should().Be("this_month");
    }

    [Fact]
    public void Explicit_dates_are_inclusive_and_used_as_given()
    {
        var resolved = At("2026-10-07T10:00:00Z").Resolve(null, Day("2026-08-31"), Day("2026-08-31"));

        resolved.Range.Should().Be(new DateRange(Day("2026-08-31"), Day("2026-08-31")));
        resolved.Range.Days.Should().Be(1);
        resolved.Requested.Should().Be(PeriodResolver.ExplicitDatesLabel);
    }

    [Fact]
    public void A_span_of_exactly_the_maximum_is_accepted()
    {
        var from = Day("2020-01-01");

        var resolved = At("2026-10-07T10:00:00Z").Resolve(null, from, from.AddDays(PeriodResolver.MaxDays - 1));

        resolved.Range.Days.Should().Be(PeriodResolver.MaxDays);
    }

    [Fact]
    public void A_span_one_day_over_the_maximum_is_refused()
    {
        var from = Day("2020-01-01");

        var act = () => At("2026-10-07T10:00:00Z").Resolve(null, from, from.AddDays(PeriodResolver.MaxDays));

        act.Should().Throw<LedgerQueryException>();
    }

    [Fact]
    public void The_last_allowed_date_is_accepted_and_the_day_after_it_is_refused()
    {
        var resolver = At("2026-10-07T10:00:00Z");

        resolver.Resolve(null, PeriodResolver.LastAllowedDate, PeriodResolver.LastAllowedDate).Range.Days.Should().Be(1);

        var act = () => resolver.Resolve(null, PeriodResolver.LastAllowedDate, PeriodResolver.LastAllowedDate.AddDays(1));

        act.Should().Throw<LedgerQueryException>().WithMessage("*9000-12-31*");
    }

    [Fact]
    public void A_period_ending_on_the_last_day_of_the_calendar_is_refused_with_a_fixed_message()
    {
        var act = () => At("2026-10-07T10:00:00Z").Resolve(null, Day("9999-12-01"), DateOnly.MaxValue);

        act.Should().Throw<LedgerQueryException>().WithMessage("Dates after 9000-12-31 are not accepted.");
    }

    [Fact]
    public void A_to_date_before_the_from_date_is_refused()
    {
        var act = () => At("2026-10-07T10:00:00Z").Resolve(null, Day("2026-08-31"), Day("2026-08-30"));

        act.Should().Throw<LedgerQueryException>();
    }

    [Fact]
    public void An_unknown_keyword_is_refused_without_repeating_it()
    {
        var act = () => At("2026-10-07T10:00:00Z").Resolve("fortnight_secret", null, null);

        act.Should().Throw<LedgerQueryException>().Which.Message.Should().NotContain("fortnight_secret");
    }

    [Fact]
    public void A_keyword_together_with_dates_is_refused()
    {
        var act = () => At("2026-10-07T10:00:00Z").Resolve("this_month", Day("2026-08-01"), null);

        act.Should().Throw<LedgerQueryException>();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Neither_or_only_one_date_is_refused(bool hasFrom, bool hasTo)
    {
        var act = () => At("2026-10-07T10:00:00Z").Resolve(
            null,
            hasFrom ? Day("2026-08-01") : null,
            hasTo ? Day("2026-08-31") : null);

        act.Should().Throw<LedgerQueryException>();
    }

    [Fact]
    public void A_pending_row_first_seen_at_half_past_eleven_in_amsterdam_counts_in_august()
    {
        var day = PeriodDate.Of(LedgerTransactionStatus.Pending, null, null, null, DateTimeOffset.Parse("2026-08-31T21:30:00Z"), Amsterdam);

        day.Should().Be(Day("2026-08-31"));
    }

    [Fact]
    public void A_pending_row_first_seen_at_half_past_midnight_in_amsterdam_counts_in_september()
    {
        var day = PeriodDate.Of(LedgerTransactionStatus.Pending, null, null, null, DateTimeOffset.Parse("2026-08-31T22:30:00Z"), Amsterdam);

        day.Should().Be(Day("2026-09-01"));
    }

    [Fact]
    public void A_pending_row_with_a_booking_date_uses_it()
    {
        var day = PeriodDate.Of(LedgerTransactionStatus.Pending, Day("2026-08-15"), null, Day("2026-08-14"), DateTimeOffset.Parse("2026-09-30T10:00:00Z"), Amsterdam);

        day.Should().Be(Day("2026-08-15"));
    }

    [Fact]
    public void A_booked_row_prefers_booking_then_value_then_transaction_date()
    {
        var seen = DateTimeOffset.Parse("2026-09-30T10:00:00Z");

        PeriodDate.Of(LedgerTransactionStatus.Booked, Day("2026-08-03"), Day("2026-08-02"), Day("2026-08-01"), seen, Amsterdam)
            .Should().Be(Day("2026-08-03"));
        PeriodDate.Of(LedgerTransactionStatus.Booked, null, Day("2026-08-02"), Day("2026-08-01"), seen, Amsterdam)
            .Should().Be(Day("2026-08-02"));
        PeriodDate.Of(LedgerTransactionStatus.Booked, null, null, Day("2026-08-01"), seen, Amsterdam)
            .Should().Be(Day("2026-08-01"));
    }

    [Fact]
    public void A_booked_row_without_any_bank_date_uses_its_amsterdam_first_seen_day()
    {
        var day = PeriodDate.Of(LedgerTransactionStatus.Booked, null, null, null, DateTimeOffset.Parse("2026-08-31T22:30:00Z"), Amsterdam);

        day.Should().Be(Day("2026-09-01"));
    }
}
