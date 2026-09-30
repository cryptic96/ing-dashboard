namespace Ledger.Domain.Ingestion;

/// <summary>What the scheduler should do for one connection at one moment.</summary>
public enum SyncDecisionKind
{
    /// <summary>Nothing is due.</summary>
    None,

    /// <summary>The day's scheduled sync is due.</summary>
    Scheduled,

    /// <summary>The single same-day retry is due.</summary>
    Retry
}

/// <summary>When the daily sync runs, in which zone, and how long after a failed scheduled run the retry may happen.</summary>
public record ScheduleSettings(TimeOnly LocalTime, TimeZoneInfo Zone, TimeSpan RetryDelay);

/// <summary>The part of a recorded run the schedule decision needs. The outcome is null while the run is unfinished.</summary>
public record SyncRunSummary(SyncTrigger Trigger, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, SyncOutcome? Outcome);

/// <summary>Converts between wall-clock times in the sync time zone and absolute instants, safely across daylight-saving changes.</summary>
public static class SyncSchedule
{
    /// <summary>Returns the absolute instant at which the given local date and time occurs in the zone.</summary>
    /// <param name="localDate">The calendar date in the zone.</param>
    /// <param name="localTime">The wall-clock time in the zone.</param>
    /// <param name="zone">The zone the wall-clock time belongs to.</param>
    /// <exception cref="ArgumentException">The local time does not exist in the zone because a clock change skips it.</exception>
    public static DateTimeOffset InstantFor(DateOnly localDate, TimeOnly localTime, TimeZoneInfo zone)
    {
        var unspecified = LocalDateTime(localDate, localTime);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>Returns the calendar date that the instant falls on in the zone, never the UTC date.</summary>
    /// <param name="instant">The absolute instant.</param>
    /// <param name="zone">The zone whose calendar to use.</param>
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    /// <summary>Returns whether the local date and time is skipped by a forward clock change in the zone.</summary>
    /// <param name="localDate">The calendar date in the zone.</param>
    /// <param name="localTime">The wall-clock time in the zone.</param>
    /// <param name="zone">The zone the wall-clock time belongs to.</param>
    public static bool IsNonexistentLocalTime(DateOnly localDate, TimeOnly localTime, TimeZoneInfo zone)
    {
        return zone.IsInvalidTime(LocalDateTime(localDate, localTime));
    }

    /// <summary>Returns the first instant of the instant's local calendar day in the zone.</summary>
    /// <param name="now">An instant on the day in question.</param>
    /// <param name="zone">The zone whose calendar to use.</param>
    public static DateTimeOffset StartOfLocalDay(DateTimeOffset now, TimeZoneInfo zone)
    {
        var candidate = LocalDateTime(LocalDate(now, zone), TimeOnly.MinValue);

        while (zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// Decides whether an automatic sync is due for one connection right now, given the runs that started on the current local day.
    /// A sync is due once the local scheduled time has passed and nothing has succeeded and nothing is running today, so a process
    /// that starts late catches up at once.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="settings">When the daily sync runs and how long to wait before the retry.</param>
    /// <param name="runsToday">Every run of the connection that started on the current local day, in any order.</param>
    public static SyncDecisionKind Decide(
        DateTimeOffset now,
        ScheduleSettings settings,
        IReadOnlyList<SyncRunSummary> runsToday)
    {
        var today = LocalDate(now, settings.Zone);

        if (runsToday.Any(run => run.FinishedAt is null || run.Outcome == SyncOutcome.Succeeded))
        {
            return SyncDecisionKind.None;
        }

        if (now < ScheduledInstant(today, settings))
        {
            return SyncDecisionKind.None;
        }

        return runsToday.Any(run => run.Trigger == SyncTrigger.Scheduled)
            ? SyncDecisionKind.None
            : SyncDecisionKind.Scheduled;
    }

    private static DateTimeOffset ScheduledInstant(DateOnly localDate, ScheduleSettings settings)
    {
        var time = settings.LocalTime;

        while (IsNonexistentLocalTime(localDate, time, settings.Zone))
        {
            time = time.AddMinutes(30);
        }

        return InstantFor(localDate, time, settings.Zone);
    }

    private static DateTime LocalDateTime(DateOnly localDate, TimeOnly localTime)
    {
        return DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Unspecified);
    }
}
