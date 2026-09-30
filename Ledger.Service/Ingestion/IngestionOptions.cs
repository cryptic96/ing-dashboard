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
}
