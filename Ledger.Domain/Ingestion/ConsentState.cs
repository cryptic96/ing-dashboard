namespace Ledger.Domain.Ingestion;

/// <summary>The stored lifecycle status of a bank connection.</summary>
public enum ConnectionStatus
{
    /// <summary>The consent is usable.</summary>
    Active,

    /// <summary>The provider reported the consent as expired.</summary>
    ProviderExpired,

    /// <summary>The consent was revoked by the operator.</summary>
    Revoked,

    /// <summary>A newer consent replaced this one.</summary>
    Superseded
}

/// <summary>The consent state shown to the operator, derived from the stored status and the validity end.</summary>
public enum ConsentView
{
    /// <summary>The consent is usable and has more than the warning period left.</summary>
    Linked,

    /// <summary>The consent is usable but ends within the warning period.</summary>
    Expiring,

    /// <summary>The consent has ended or the provider reported it expired.</summary>
    Expired,

    /// <summary>The consent was revoked.</summary>
    Revoked,

    /// <summary>A newer consent replaced this one.</summary>
    Superseded
}

/// <summary>A derived consent state and the time left until the consent ends, negative once it has ended.</summary>
public record ConsentSnapshot(ConsentView State, double DaysUntilExpiry);

/// <summary>Derives the consent state from what is stored and the current time, rather than trusting a stored label.</summary>
public static class ConsentState
{
    /// <summary>The default number of days before the end at which a consent counts as expiring.</summary>
    public const int DefaultWarnDays = 14;

    /// <summary>
    /// Derives the state: revoked and superseded connections keep those states, a consent the provider reported expired is
    /// expired, and an active one is expired once its end is reached, expiring when less than the warning period remains
    /// and linked otherwise. Exactly the warning period left is still linked.
    /// </summary>
    public static ConsentSnapshot Derive(
        ConnectionStatus status,
        DateTimeOffset validUntil,
        DateTimeOffset now,
        int warnDays = DefaultWarnDays)
    {
        var remaining = validUntil - now;

        var view = status switch
        {
            ConnectionStatus.Revoked => ConsentView.Revoked,
            ConnectionStatus.Superseded => ConsentView.Superseded,
            ConnectionStatus.ProviderExpired => ConsentView.Expired,
            _ when remaining <= TimeSpan.Zero => ConsentView.Expired,
            _ when remaining < TimeSpan.FromDays(warnDays) => ConsentView.Expiring,
            _ => ConsentView.Linked
        };

        return new ConsentSnapshot(view, remaining.TotalDays);
    }
}
