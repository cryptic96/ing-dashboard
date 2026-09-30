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
}
