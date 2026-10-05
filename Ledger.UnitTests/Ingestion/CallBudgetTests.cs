using FluentAssertions;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the per-account background call allowance at the edges of both window readings.</summary>
[Trait("Category", "Sync")]
public class CallBudgetTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Three_calls_inside_the_rolling_day_leave_one_of_four()
    {
        var calls = new[] { Now.AddHours(-24).AddMinutes(1), Now.AddHours(-1), Now.AddMinutes(-5) };

        CallBudget.Remaining(4, calls, Now, QuotaWindow.Rolling24Hours, Amsterdam).Should().Be(1);
    }

    [Fact]
    public void A_call_exactly_twenty_four_hours_old_no_longer_counts()
    {
        var calls = new[] { Now.AddHours(-24), Now.AddHours(-1), Now.AddMinutes(-5) };

        CallBudget.Remaining(4, calls, Now, QuotaWindow.Rolling24Hours, Amsterdam).Should().Be(2);
    }

    [Fact]
    public void A_call_one_minute_short_of_twenty_four_hours_old_still_counts()
    {
        var calls = new[] { Now.AddHours(-24).AddMinutes(1) };

        CallBudget.Remaining(1, calls, Now, QuotaWindow.Rolling24Hours, Amsterdam).Should().Be(0);
    }

    [Fact]
    public void The_remaining_calls_never_go_below_zero()
    {
        var calls = Enumerable.Range(1, 6).Select(minute => Now.AddMinutes(-minute)).ToList();

        CallBudget.Remaining(4, calls, Now, QuotaWindow.Rolling24Hours, Amsterdam).Should().Be(0);
    }

    [Fact]
    public void No_calls_leave_the_whole_allowance()
    {
        CallBudget.Remaining(4, [], Now, QuotaWindow.Rolling24Hours, Amsterdam).Should().Be(4);
    }

    [Fact]
    public void The_calendar_day_window_counts_only_calls_on_the_current_Amsterdam_date()
    {
        var localMidnight = new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero);
        var calls = new[] { localMidnight.AddMinutes(-1), localMidnight, localMidnight.AddHours(3) };

        CallBudget.Remaining(4, calls, Now, QuotaWindow.LocalCalendarDay, Amsterdam).Should().Be(2);
    }

    [Fact]
    public void The_calendar_day_window_follows_the_local_date_not_the_utc_date()
    {
        var justAfterLocalMidnight = new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero);
        var calls = new[] { new DateTimeOffset(2026, 10, 25, 21, 30, 0, TimeSpan.Zero) };

        CallBudget.Remaining(4, calls, justAfterLocalMidnight, QuotaWindow.LocalCalendarDay, Amsterdam).Should().Be(4);
    }

    [Fact]
    public void The_window_start_is_twenty_four_hours_back_or_local_midnight()
    {
        CallBudget.WindowStart(Now, QuotaWindow.Rolling24Hours, Amsterdam).Should().Be(Now.AddHours(-24));
        CallBudget.WindowStart(Now, QuotaWindow.LocalCalendarDay, Amsterdam)
            .Should().Be(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero));
    }
}
