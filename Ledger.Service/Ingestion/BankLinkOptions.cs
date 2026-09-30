namespace Ledger.Service.Ingestion;

/// <summary>Settings of the guided bank link flow, bound from the "BankLink" configuration section.</summary>
public class BankLinkOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "BankLink";

    /// <summary>
    /// The absolute https address the bank redirects the browser to after approval. It must be the callback route of this
    /// application, reachable from the browser that approves the consent.
    /// </summary>
    public string? RedirectUrl { get; set; }

    /// <summary>How long a link request stays valid, in minutes.</summary>
    public int StateLifetimeMinutes { get; set; } = 15;

    /// <summary>The name of the bank being linked, recorded with each connection.</summary>
    public string AspspName { get; set; } = "ING";

    /// <summary>The two-letter country code of the bank being linked, recorded with each connection.</summary>
    public string AspspCountry { get; set; } = "NL";
}
