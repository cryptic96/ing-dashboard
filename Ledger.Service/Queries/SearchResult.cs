using System.Text.Json.Serialization;

namespace Ledger.Service.Queries;

/// <summary>
/// What the transaction search tool is asked. Every field is optional text or a number chosen by Claude and is validated before
/// use. Dates are calendar dates written as yyyy-MM-dd.
/// </summary>
public record SearchRequest(
    string? Period,
    string? FromDate,
    string? ToDate,
    IReadOnlyList<string>? Counterparty,
    IReadOnlyList<string>? CounterpartyRef,
    IReadOnlyList<string>? Description,
    IReadOnlyList<string>? Accounts,
    string? Direction,
    decimal? MinAmount,
    decimal? MaxAmount,
    string? Status,
    int? Limit,
    string? Cursor);

/// <summary>
/// What the transaction search tool returns: one page of detail rows, newest first, with how many rows matched in all and whether
/// the page was cut short. It never carries a sum of money; totals come from the money totals tool. Counterparty account numbers
/// are masked and the household's own account numbers never appear.
/// </summary>
public record SearchResult(
    [property: JsonPropertyName("period")] TotalsPeriod Period,
    [property: JsonPropertyName("filters")] SearchFilters Filters,
    [property: JsonPropertyName("rows")] IReadOnlyList<SearchRow> Rows,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("matching_total")] int MatchingTotal,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("limit_clamped")] bool LimitClamped,
    [property: JsonPropertyName("note")] string Note);

/// <summary>The filters that were applied, normalised, so a search can be repeated.</summary>
public record SearchFilters(
    [property: JsonPropertyName("counterparty")] IReadOnlyList<string> Counterparty,
    [property: JsonPropertyName("counterparty_ref")] IReadOnlyList<string> CounterpartyRef,
    [property: JsonPropertyName("description")] IReadOnlyList<string> Description,
    [property: JsonPropertyName("accounts")] IReadOnlyList<string> Accounts,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("min_amount")] string? MinAmount,
    [property: JsonPropertyName("max_amount")] string? MaxAmount,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// One transaction. The amount is signed exact text, negative for money out. The counterparty account is masked to its last four
/// characters. Internal transfer is true for a booked move between the household's own synced accounts.
/// </summary>
public record SearchRow(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("booking_date"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BookingDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("counterparty_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CounterpartyName,
    [property: JsonPropertyName("counterparty_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CounterpartyRef,
    [property: JsonPropertyName("counterparty_account"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CounterpartyAccount,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("account_key")] string AccountKey,
    [property: JsonPropertyName("account_name")] string AccountName,
    [property: JsonPropertyName("internal_transfer")] bool InternalTransfer);
