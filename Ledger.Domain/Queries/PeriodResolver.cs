using System.Globalization;
using Ledger.Domain.Ingestion;

namespace Ledger.Domain.Queries;

/// <summary>An inclusive range of calendar days.</summary>
/// <param name="From">The first day.</param>
/// <param name="To">The last day.</param>
public record DateRange(DateOnly From, DateOnly To)
{
    /// <summary>The number of calendar days in the range, counting both ends.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>The periods that can be asked for by name.</summary>
public enum RelativePeriod
{
    /// <summary>The current day.</summary>
    Today,

    /// <summary>The day before the current day.</summary>
    Yesterday,

    /// <summary>Monday to Sunday of the current week.</summary>
    ThisWeek,

    /// <summary>Monday to Sunday of the previous week.</summary>
    LastWeek,

    /// <summary>The whole current calendar month.</summary>
    ThisMonth,

    /// <summary>The whole previous calendar month.</summary>
    LastMonth,

    /// <summary>The last 30 days, ending today.</summary>
    Last30Days,

    /// <summary>The last 90 days, ending today.</summary>
    Last90Days,

    /// <summary>The whole current calendar year.</summary>
    ThisYear,

    /// <summary>The whole previous calendar year.</summary>
    LastYear
}

/// <summary>A period as the caller asked for it and as it resolved. Requested is the keyword, or the fixed text for explicit dates.</summary>
/// <param name="Range">The resolved inclusive days.</param>
/// <param name="Requested">The keyword that was asked for, or <see cref="PeriodResolver.ExplicitDatesLabel"/>.</param>
public record ResolvedPeriod(DateRange Range, string Requested);

/// <summary>Turns a period keyword or a pair of dates into absolute calendar days in the configured time zone.</summary>
public class PeriodResolver(TimeProvider timeProvider, TimeZoneInfo zone)
{
    /// <summary>The longest span that can be asked for, in days.</summary>
    public const int MaxDays = 3660;

    /// <summary>The last day an explicit date may name, which keeps day, week and month arithmetic clear of the calendar's end.</summary>
    public static readonly DateOnly LastAllowedDate = new(9000, 12, 31);

    /// <summary>The value reported as the requested period when explicit dates were given.</summary>
    public const string ExplicitDatesLabel = "explicit_dates";

    /// <summary>Returns today's date in the configured zone, never the UTC date.</summary>
    public DateOnly Today()
    {
        return SyncSchedule.LocalDate(timeProvider.GetUtcNow(), zone);
    }

    /// <summary>
    /// Resolves exactly one of a keyword or an explicit pair of dates. Explicit dates are inclusive calendar dates used as given.
    /// </summary>
    /// <param name="period">The keyword, for example this_month, or null.</param>
    /// <param name="fromDate">The first day, or null when a keyword is used.</param>
    /// <param name="toDate">The last day, or null when a keyword is used.</param>
    /// <exception cref="LedgerQueryException">The request is not exactly one keyword or both dates, or the span is invalid.</exception>
    public ResolvedPeriod Resolve(string? period, DateOnly? fromDate, DateOnly? toDate)
    {
        var hasKeyword = !string.IsNullOrWhiteSpace(period);
        var hasDates = fromDate is not null || toDate is not null;

        if (hasKeyword && hasDates)
        {
            throw new LedgerQueryException("Give either a period keyword or fromDate and toDate, not both.");
        }

        if (hasKeyword)
        {
            var keyword = Parse(period!);

            return new ResolvedPeriod(Range(keyword, Today()), KeywordText(keyword));
        }

        if (fromDate is not { } from || toDate is not { } to)
        {
            throw new LedgerQueryException("Give a period keyword, or both fromDate and toDate.");
        }

        if (to < from)
        {
            throw new LedgerQueryException("toDate must not be before fromDate.");
        }

        if (to > LastAllowedDate)
        {
            throw new LedgerQueryException("Dates after 9000-12-31 are not accepted.");
        }

        var explicitRange = new DateRange(from, to);

        if (explicitRange.Days > MaxDays)
        {
            throw new LedgerQueryException("The period may span at most ten years.");
        }

        return new ResolvedPeriod(explicitRange, ExplicitDatesLabel);
    }

    /// <summary>Returns the lower-snake-case keyword for a relative period, for example Last30Days becomes last_30_days.</summary>
    /// <param name="period">The relative period.</param>
    public static string KeywordText(RelativePeriod period)
    {
        return period switch
        {
            RelativePeriod.Today => "today",
            RelativePeriod.Yesterday => "yesterday",
            RelativePeriod.ThisWeek => "this_week",
            RelativePeriod.LastWeek => "last_week",
            RelativePeriod.ThisMonth => "this_month",
            RelativePeriod.LastMonth => "last_month",
            RelativePeriod.Last30Days => "last_30_days",
            RelativePeriod.Last90Days => "last_90_days",
            RelativePeriod.ThisYear => "this_year",
            _ => "last_year"
        };
    }

    private static RelativePeriod Parse(string keyword)
    {
        var text = keyword.Trim().ToLower(CultureInfo.InvariantCulture);

        foreach (var candidate in Enum.GetValues<RelativePeriod>())
        {
            if (KeywordText(candidate) == text)
            {
                return candidate;
            }
        }

        throw new LedgerQueryException(
            "The period keyword is not known. Use today, yesterday, this_week, last_week, this_month, last_month, "
            + "last_30_days, last_90_days, this_year or last_year, or give fromDate and toDate.");
    }

    private static DateRange Range(RelativePeriod period, DateOnly today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var firstOfLastMonth = firstOfMonth.AddMonths(-1);

        return period switch
        {
            RelativePeriod.Today => new DateRange(today, today),
            RelativePeriod.Yesterday => new DateRange(today.AddDays(-1), today.AddDays(-1)),
            RelativePeriod.ThisWeek => new DateRange(monday, monday.AddDays(6)),
            RelativePeriod.LastWeek => new DateRange(monday.AddDays(-7), monday.AddDays(-1)),
            RelativePeriod.ThisMonth => new DateRange(firstOfMonth, firstOfMonth.AddMonths(1).AddDays(-1)),
            RelativePeriod.LastMonth => new DateRange(firstOfLastMonth, firstOfMonth.AddDays(-1)),
            RelativePeriod.Last30Days => new DateRange(today.AddDays(-29), today),
            RelativePeriod.Last90Days => new DateRange(today.AddDays(-89), today),
            RelativePeriod.ThisYear => new DateRange(new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            _ => new DateRange(new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31))
        };
    }
}
