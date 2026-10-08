using Ledger.Domain.Queries;

namespace Ledger.Service.Queries;

/// <summary>
/// Resolves the configured time zone for a query. The database is given the zone's IANA name, so a Windows zone name is
/// converted, and a zone that does not exist is refused with a fixed message instead of failing every query with a generic error.
/// </summary>
public static class QueryTimeZone
{
    private const string NotUsableMessage =
        "The ledger's time zone is not set to a valid IANA zone such as Europe/Amsterdam. Ask the operator to correct it.";

    /// <summary>Returns the time zone for the configured name, with an IANA identifier.</summary>
    /// <param name="zoneName">The configured zone name, as an IANA name or a Windows name.</param>
    /// <exception cref="LedgerQueryException">The name is blank, unknown on this system or not usable.</exception>
    public static TimeZoneInfo Resolve(string? zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            throw new LedgerQueryException(NotUsableMessage);
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName.Trim());

            return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId)
                ? TimeZoneInfo.FindSystemTimeZoneById(ianaId)
                : zone;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new LedgerQueryException(NotUsableMessage);
        }
    }
}
