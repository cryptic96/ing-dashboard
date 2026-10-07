using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ledger.Domain.Queries;

/// <summary>Which transactions a search shows by status. Dropped transactions are never shown.</summary>
public enum SearchStatus
{
    /// <summary>Booked transactions only.</summary>
    Booked,

    /// <summary>Pending transactions only.</summary>
    Pending,

    /// <summary>Booked and pending transactions.</summary>
    Both
}

/// <summary>The sort key of the last row of a page and the hash of the search it belongs to: where the next page continues.</summary>
/// <param name="PeriodDate">The period date of the last row.</param>
/// <param name="FirstSeenAt">The instant the ledger first saw the last row.</param>
/// <param name="Id">The internal identifier of the last row.</param>
/// <param name="FilterHash">The hash of the filters and status the cursor was issued for.</param>
public record SearchPosition(DateOnly PeriodDate, DateTimeOffset FirstSeenAt, Guid Id, string FilterHash);

/// <summary>
/// An opaque keyset cursor for paging through search results. It carries the sort key of the last row shown and a hash of the
/// filters, so it can only continue the search it came from. It is not a secret: it only detects misuse, and a forged cursor can
/// only select rows the filters already allow.
/// </summary>
public static class SearchCursor
{
    private const int HashLength = 16;
    private const int MaxCursorLength = 512;

    /// <summary>Encodes the key of the last row of a page and the filter hash as base64url of compact JSON.</summary>
    /// <param name="periodDate">The period date of the last row.</param>
    /// <param name="firstSeenAt">The instant the ledger first saw the last row.</param>
    /// <param name="id">The internal identifier of the last row.</param>
    /// <param name="filterHash">The hash of the filters and status, from <see cref="FilterHash"/>.</param>
    public static string Encode(DateOnly periodDate, DateTimeOffset firstSeenAt, Guid id, string filterHash)
    {
        var json = JsonSerializer.Serialize(new CursorBody
        {
            Date = periodDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Ticks = firstSeenAt.UtcTicks,
            Id = id.ToString("D"),
            Hash = filterHash
        });

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes a cursor; returns false for any text that is not a cursor this class produced.</summary>
    /// <param name="cursor">The cursor text.</param>
    /// <param name="position">The decoded position when the result is true.</param>
    public static bool TryDecode(string cursor, out SearchPosition position)
    {
        position = null!;

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxCursorLength)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');

            var body = JsonSerializer.Deserialize<CursorBody>(Convert.FromBase64String(padded));

            if (body is null
                || body.Date is null
                || body.Ticks is null
                || body.Id is null
                || body.Hash is null
                || body.Hash.Length != HashLength
                || body.Ticks < 0
                || body.Ticks > DateTime.MaxValue.Ticks
                || !DateOnly.TryParseExact(body.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !Guid.TryParseExact(body.Id, "D", out var id))
            {
                return false;
            }

            position = new SearchPosition(date, new DateTimeOffset(body.Ticks.Value, TimeSpan.Zero), id, body.Hash);

            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns 16 hex characters of the SHA-256 of a canonical rendering of the filter and status. The rendering sorts every list, so
    /// the same search written with its terms in another order has the same hash.
    /// </summary>
    /// <param name="filter">The filter of the search.</param>
    /// <param name="status">The status selection of the search.</param>
    public static string FilterHash(LedgerQueryFilter filter, string status)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            filter.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            filter.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Sorted(filter.AccountKeys),
            Sorted(filter.CounterpartyTerms),
            Sorted(filter.CounterpartyNames),
            Sorted(filter.CounterpartyRefs ?? []),
            Sorted(filter.DescriptionTerms),
            filter.Direction.ToString(),
            filter.MinAmount is { } min ? MoneyText.Format(min) : null,
            filter.MaxAmount is { } max ? MoneyText.Format(max) : null,
            status
        });

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..HashLength];
    }

    private static string[] Sorted(IReadOnlyList<string> values)
    {
        return values.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class CursorBody
    {
        [JsonPropertyName("d")]
        public string? Date { get; set; }

        [JsonPropertyName("t")]
        public long? Ticks { get; set; }

        [JsonPropertyName("i")]
        public string? Id { get; set; }

        [JsonPropertyName("h")]
        public string? Hash { get; set; }
    }
}
