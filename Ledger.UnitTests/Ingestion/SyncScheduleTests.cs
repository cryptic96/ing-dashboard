using FluentAssertions;
using Ledger.Domain.Ingestion;
using Microsoft.Extensions.Time.Testing;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the Amsterdam wall-clock schedule math across daylight-saving changes and midnight.</summary>
[Trait("Category", "Scheduler")]
public class SyncScheduleTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Fact]
    public void Instant_for_morning_run_on_autumn_changeover_day_uses_winter_offset_after_the_change()
    {
        var instant = SyncSchedule.InstantFor(new DateOnly(2026, 10, 25), new TimeOnly(6, 30), Amsterdam);

        instant.Should().Be(new DateTimeOffset(2026, 10, 25, 5, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Instant_for_morning_run_on_spring_changeover_day_uses_summer_offset_after_the_change()
    {
        var instant = SyncSchedule.InstantFor(new DateOnly(2027, 3, 28), new TimeOnly(6, 30), Amsterdam);

        instant.Should().Be(new DateTimeOffset(2027, 3, 28, 4, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Instant_for_an_ordinary_summer_day_uses_the_summer_offset()
    {
        var instant = SyncSchedule.InstantFor(new DateOnly(2026, 7, 1), new TimeOnly(6, 30), Amsterdam);

        instant.Should().Be(new DateTimeOffset(2026, 7, 1, 4, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Local_time_skipped_by_the_spring_change_is_reported_as_nonexistent()
    {
        SyncSchedule.IsNonexistentLocalTime(new DateOnly(2027, 3, 28), new TimeOnly(2, 30), Amsterdam)
            .Should().BeTrue();
    }

    [Fact]
    public void Morning_local_time_on_the_spring_change_day_exists()
    {
        SyncSchedule.IsNonexistentLocalTime(new DateOnly(2027, 3, 28), new TimeOnly(6, 30), Amsterdam)
            .Should().BeFalse();
    }

    [Fact]
    public void Local_date_one_second_before_local_midnight_is_still_the_old_day()
    {
        var instant = new DateTimeOffset(2026, 12, 31, 22, 59, 59, TimeSpan.Zero);

        SyncSchedule.LocalDate(instant, Amsterdam).Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Local_date_at_local_midnight_is_the_new_day()
    {
        var instant = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero);

        SyncSchedule.LocalDate(instant, Amsterdam).Should().Be(new DateOnly(2027, 1, 1));
    }

    [Theory]
    [InlineData(2026, 10, 26)]
    [InlineData(2026, 10, 25)]
    [InlineData(2027, 3, 28)]
    [InlineData(2026, 7, 1)]
    public void Decide_is_none_one_minute_before_the_scheduled_time_and_scheduled_at_it(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);

        SyncSchedule.Decide(LocalInstant(date, 6, 29), Settings(), []).Should().Be(SyncDecisionKind.None);
        SyncSchedule.Decide(LocalInstant(date, 6, 30), Settings(), []).Should().Be(SyncDecisionKind.Scheduled);
    }

    [Fact]
    public void Decide_is_none_after_a_successful_run_today_whatever_started_it()
    {
        var date = new DateOnly(2026, 10, 26);
        var runs = new[] { Run(SyncTrigger.Manual, date, 6, 40, 6, 41, SyncOutcome.Succeeded) };

        SyncSchedule.Decide(LocalInstant(date, 9, 0), Settings(), runs).Should().Be(SyncDecisionKind.None);
    }

    [Fact]
    public void Decide_is_none_while_a_run_is_unfinished()
    {
        var date = new DateOnly(2026, 10, 26);
        var runs = new[] { new SyncRunSummary(SyncTrigger.Scheduled, LocalInstant(date, 6, 30), null, null) };

        SyncSchedule.Decide(LocalInstant(date, 6, 31), Settings(), runs).Should().Be(SyncDecisionKind.None);
    }

    [Fact]
    public void Decide_catches_up_at_once_when_the_process_starts_after_the_scheduled_time_with_no_run_today()
    {
        var date = new DateOnly(2026, 10, 26);

        SyncSchedule.Decide(LocalInstant(date, 9, 0), Settings(), []).Should().Be(SyncDecisionKind.Scheduled);
    }

    [Theory]
    [InlineData(SyncOutcome.FailedTransient)]
    [InlineData(SyncOutcome.FailedMalformed)]
    [InlineData(SyncOutcome.Abandoned)]
    public void Decide_retries_once_four_hours_after_a_temporarily_failed_scheduled_run_and_not_a_minute_earlier(SyncOutcome outcome)
    {
        var date = new DateOnly(2026, 10, 26);
        var runs = new[] { Run(SyncTrigger.Scheduled, date, 6, 30, 6, 35, outcome) };

        SyncSchedule.Decide(LocalInstant(date, 10, 34), Settings(), runs).Should().Be(SyncDecisionKind.None);
        SyncSchedule.Decide(LocalInstant(date, 10, 35), Settings(), runs).Should().Be(SyncDecisionKind.Retry);
    }

    [Fact]
    public void Decide_is_none_three_hours_fifty_nine_minutes_after_the_failure_and_retry_at_four_hours()
    {
        var date = new DateOnly(2026, 10, 26);
        var runs = new[] { Run(SyncTrigger.Scheduled, date, 6, 30, 7, 0, SyncOutcome.FailedTransient) };
        var finished = LocalInstant(date, 7, 0);

        SyncSchedule.Decide(finished + TimeSpan.FromMinutes(239), Settings(), runs).Should().Be(SyncDecisionKind.None);
        SyncSchedule.Decide(finished + TimeSpan.FromHours(4), Settings(), runs).Should().Be(SyncDecisionKind.Retry);
    }

    [Fact]
    public void Decide_never_retries_again_once_a_retry_run_exists_today()
    {
        var date = new DateOnly(2026, 10, 26);
        var runs = new[]
        {
            Run(SyncTrigger.Scheduled, date, 6, 30, 6, 35, SyncOutcome.FailedTransient),
            Run(SyncTrigger.Retry, date, 10, 35, 10, 40, SyncOutcome.FailedTransient)
        };

        SyncSchedule.Decide(LocalInstant(date, 20, 0), Settings(), runs).Should().Be(SyncDecisionKind.None);
    }

    [Fact]
    public void Decide_gives_no_retry_when_the_retry_moment_falls_on_the_next_local_day()
    {
        var date = new DateOnly(2026, 10, 26);
        var runs = new[] { Run(SyncTrigger.Scheduled, date, 20, 55, 21, 0, SyncOutcome.FailedTransient) };

        SyncSchedule.Decide(LocalInstant(date, 23, 59), Settings(), runs).Should().Be(SyncDecisionKind.None);
        SyncSchedule.Decide(LocalInstant(date.AddDays(1), 1, 0), Settings(), runs).Should().Be(SyncDecisionKind.None);
    }

    [Theory]
    [InlineData(SyncOutcome.FailedRateLimited)]
    [InlineData(SyncOutcome.QuotaExhausted)]
    [InlineData(SyncOutcome.FailedConsent)]
    [InlineData(SyncOutcome.FailedProviderAuth)]
    public void Decide_is_none_for_the_rest_of_the_day_after_a_refusal_even_for_a_scheduled_run_that_has_not_happened_yet(SyncOutcome outcome)
    {
        var date = new DateOnly(2026, 10, 26);
        var manualRefusal = new[] { Run(SyncTrigger.Manual, date, 5, 0, 5, 1, outcome) };
        var scheduledRefusal = new[] { Run(SyncTrigger.Scheduled, date, 6, 30, 6, 31, outcome) };

        SyncSchedule.Decide(LocalInstant(date, 7, 0), Settings(), manualRefusal).Should().Be(SyncDecisionKind.None);
        SyncSchedule.Decide(LocalInstant(date, 23, 0), Settings(), scheduledRefusal).Should().Be(SyncDecisionKind.None);
    }

    [Fact]
    public void Decide_starts_the_next_day_afresh_after_a_refusal_yesterday()
    {
        var yesterday = new DateOnly(2026, 10, 26);
        var today = yesterday.AddDays(1);

        SyncSchedule.Decide(LocalInstant(today, 6, 30), Settings(), []).Should().Be(SyncDecisionKind.Scheduled);
        SyncSchedule.Decide(LocalInstant(yesterday, 23, 0), Settings(), [Run(SyncTrigger.Scheduled, yesterday, 6, 30, 6, 31, SyncOutcome.FailedRateLimited)])
            .Should().Be(SyncDecisionKind.None);
    }

    [Fact]
    public void Start_of_local_day_is_local_midnight_even_when_the_day_has_an_extra_hour()
    {
        var start = SyncSchedule.StartOfLocalDay(LocalInstant(new DateOnly(2026, 10, 25), 12, 0), Amsterdam);

        start.Should().Be(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Local_date_follows_the_zone_rather_than_utc_across_the_autumn_change()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero));

        var before = SyncSchedule.LocalDate(clock.GetUtcNow(), Amsterdam);
        clock.Advance(TimeSpan.FromHours(1));
        var after = SyncSchedule.LocalDate(clock.GetUtcNow(), Amsterdam);

        before.Should().Be(new DateOnly(2026, 10, 25));
        after.Should().Be(new DateOnly(2026, 10, 25));
    }

    private static DateTimeOffset LocalInstant(DateOnly date, int hour, int minute)
    {
        return SyncSchedule.InstantFor(date, new TimeOnly(hour, minute), Amsterdam);
    }

    private static ScheduleSettings Settings()
    {
        return new ScheduleSettings(new TimeOnly(6, 30), Amsterdam, TimeSpan.FromHours(4));
    }

    private static SyncRunSummary Run(
        SyncTrigger trigger,
        DateOnly date,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        SyncOutcome outcome)
    {
        return new SyncRunSummary(trigger, LocalInstant(date, startHour, startMinute), LocalInstant(date, endHour, endMinute), outcome);
    }
}
