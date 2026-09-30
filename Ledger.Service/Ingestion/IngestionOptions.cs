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
}
