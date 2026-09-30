namespace Ledger.Repository.Entities;

/// <summary>One approved consent at a bank data provider. The session id is stored only in protected form.</summary>
public class BankConnectionEntity
{
    /// <summary>The stored values of <see cref="Status"/>.</summary>
    public static class Statuses
    {
        /// <summary>The consent is usable.</summary>
        public const string Active = "active";

        /// <summary>The provider reported the consent as expired.</summary>
        public const string ProviderExpired = "provider_expired";

        /// <summary>The consent was revoked.</summary>
        public const string Revoked = "revoked";

        /// <summary>A newer consent replaced this one.</summary>
        public const string Superseded = "superseded";
    }

    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>A random opaque key that names the connection without exposing any bank identifier.</summary>
    public string ConnectionKey { get; set; } = string.Empty;

    /// <summary>The short name of the provider that issued the consent.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The bank's name as the provider reports it.</summary>
    public string AspspName { get; set; } = string.Empty;

    /// <summary>The bank's two-letter country code.</summary>
    public string AspspCountry { get; set; } = string.Empty;

    /// <summary>The lifecycle status, one of <see cref="Statuses"/>.</summary>
    public string Status { get; set; } = Statuses.Active;

    /// <summary>The session id, encrypted with the application's secret protector. Never logged or returned.</summary>
    public string SessionIdProtected { get; set; } = string.Empty;

    /// <summary>When the consent was approved.</summary>
    public DateTimeOffset AuthorizedAt { get; set; }

    /// <summary>When the consent ends.</summary>
    public DateTimeOffset ValidUntil { get; set; }

    /// <summary>The connection that replaced this one, if any.</summary>
    public Guid? SupersededById { get; set; }

    /// <summary>When the connection was closed, if it was.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>When the row was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
