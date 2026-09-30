namespace Ledger.Domain.Ingestion;

/// <summary>How the per-account allowance of background calls is measured.</summary>
public enum QuotaWindow
{
    /// <summary>The trailing 24 hours: a call stops counting exactly 24 hours after it was made.</summary>
    Rolling24Hours,

    /// <summary>The current calendar day in the sync time zone.</summary>
    LocalCalendarDay
}

/// <summary>Computes how many background calls an account may still make.</summary>
public static class CallBudget
{
    /// <summary>
    /// Returns how many background calls remain, never below zero. In the rolling window a call counts while it is newer than
    /// 24 hours and not in the future; in the calendar-day window it counts while it is on the current local date.
    /// </summary>
    /// <param name="limit">The most background calls allowed in the window.</param>
    /// <param name="backgroundCallTimes">The times of the account's earlier background calls.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="window">How the window is measured.</param>
    /// <param name="zone">The zone whose calendar the calendar-day window uses.</param>
    public static int Remaining(
        int limit,
        IReadOnlyList<DateTimeOffset> backgroundCallTimes,
        DateTimeOffset now,
        QuotaWindow window,
        TimeZoneInfo zone)
    {
        var used = window == QuotaWindow.Rolling24Hours
            ? backgroundCallTimes.Count(time => time > now.AddHours(-24) && time <= now)
            : backgroundCallTimes.Count(time => SyncSchedule.LocalDate(time, zone) == SyncSchedule.LocalDate(now, zone));

        return Math.Max(0, limit - used);
    }

    /// <summary>Returns the earliest instant whose calls may still count at the given moment, for limiting what is read from storage.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="window">How the window is measured.</param>
    /// <param name="zone">The zone whose calendar the calendar-day window uses.</param>
    public static DateTimeOffset WindowStart(DateTimeOffset now, QuotaWindow window, TimeZoneInfo zone)
    {
        return window == QuotaWindow.Rolling24Hours
            ? now.AddHours(-24)
            : SyncSchedule.StartOfLocalDay(now, zone);
    }
}
