using System.Globalization;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;

namespace Ledger.Service.Ingestion;

/// <summary>Tunable ingestion behaviour, bound from the "Ingestion" configuration section.</summary>
public class IngestionOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Ingestion";

    /// <summary>How many days before the latest known transaction an incremental fetch starts, so boundary items are seen again.</summary>
    public int OverlapDays { get; set; } = 14;

    /// <summary>How many days apart a pending and a booked item may be and still be considered the same transaction.</summary>
    public int MatchWindowDays { get; set; } = 5;

    /// <summary>The provider names accepted by <see cref="Provider"/>.</summary>
    public static class Providers
    {
        /// <summary>No provider: bank linking is not configured.</summary>
        public const string None = "None";

        /// <summary>A scripted synthetic bank, for local development only.</summary>
        public const string Synthetic = "Synthetic";

        /// <summary>The Enable Banking aggregator.</summary>
        public const string EnableBanking = "EnableBanking";
    }

    /// <summary>Which bank data provider to use: None (the default), Synthetic or EnableBanking.</summary>
    public string Provider { get; set; } = Providers.None;

    /// <summary>
    /// Whether an operator-triggered sync passes the operator's client address and User-Agent to the provider, which lets
    /// providers that distinguish attended access treat it as such.
    /// </summary>
    public bool PsuHeadersOnOperatorSyncs { get; set; } = true;

    /// <summary>The wall-clock time, as HH:mm in <see cref="TimeZone"/>, at which the daily sync runs.</summary>
    public string ScheduleLocalTime { get; set; } = "06:30";

    /// <summary>The time zone that defines the day and the schedule, as a system time zone id.</summary>
    public string TimeZone { get; set; } = "Europe/Amsterdam";

    /// <summary>How many hours after a failed scheduled run the single same-day retry may happen.</summary>
    public int RetryDelayHours { get; set; } = 4;

    /// <summary>How many background calls the bank allows per account in the quota window. Every page and balances read counts.</summary>
    public int BackgroundCallsPerDay { get; set; } = 4;

    /// <summary>How the quota window is measured: Rolling24Hours (the default, the conservative reading) or LocalCalendarDay.</summary>
    public QuotaWindow QuotaWindow { get; set; } = QuotaWindow.Rolling24Hours;

    /// <summary>
    /// The balance kinds the ledger reconciles against its booked transactions, in order of preference. The first one the bank
    /// returns with a reference date is used.
    /// </summary>
    public List<BalanceKind> ReconcileBalanceKinds { get; set; } = [BalanceKind.ClosingBooked, BalanceKind.InterimBooked];

    /// <summary>Whether the background scheduler runs. Turning it off leaves operator-triggered syncs working.</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>Resolves <see cref="TimeZone"/> to a time zone.</summary>
    /// <exception cref="TimeZoneNotFoundException">The configured zone does not exist on this system.</exception>
    public TimeZoneInfo ResolveTimeZone()
    {
        return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
    }

    /// <summary>Parses <see cref="ScheduleLocalTime"/> as HH:mm.</summary>
    /// <exception cref="FormatException">The configured time is not in the HH:mm form.</exception>
    public TimeOnly ParseScheduleLocalTime()
    {
        return TimeOnly.ParseExact(ScheduleLocalTime, "HH:mm", CultureInfo.InvariantCulture);
    }
}
