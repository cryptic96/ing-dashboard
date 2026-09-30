namespace Ledger.Domain.Ingestion;

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

    private static DateTime LocalDateTime(DateOnly localDate, TimeOnly localTime)
    {
        return DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Unspecified);
    }
}
