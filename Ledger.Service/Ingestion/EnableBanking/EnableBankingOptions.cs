namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>Settings of the Enable Banking aggregator, bound from the "EnableBanking" configuration section.</summary>
public class EnableBankingOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "EnableBanking";

    /// <summary>The address every request is sent to. It is a constant on purpose: the signed token must only ever reach the aggregator.</summary>
    public static readonly Uri BaseAddress = new("https://api.enablebanking.com");

    /// <summary>The identifier of the registered aggregator application. It becomes the key id of every client token.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>The path of the PEM file holding the application's private key.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The password protecting the private key file, or empty when the key is not encrypted.</summary>
    public string? PrivateKeyPassword { get; set; }

    /// <summary>The name of the bank to link.</summary>
    public string AspspName { get; set; } = "ING";

    /// <summary>The two-letter country of the bank to link.</summary>
    public string AspspCountry { get; set; } = "NL";

    /// <summary>How many days a new consent should last. The bank's own maximum caps it.</summary>
    public int ConsentValidityDays { get; set; } = 180;

    /// <summary>
    /// The hosts the operator may be sent to for approving a consent. A host matches when it equals an entry or ends with a dot
    /// followed by an entry.
    /// </summary>
    public List<string> AuthorizationHosts { get; set; } = ["enablebanking.com"];

    /// <summary>How long one request to the aggregator may take before it is treated as a temporary failure.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;
}
