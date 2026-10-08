using System.Text.Json.Serialization;

namespace Ledger.Service.Queries;

/// <summary>
/// What the counterparty lookup tool is asked. The text is matched against counterparty names; the period is optional and a lookup
/// without one covers all history. Dates are calendar dates written as yyyy-MM-dd.
/// </summary>
public record CounterpartiesRequest(
    string? Text,
    string? Period,
    string? FromDate,
    string? ToDate,
    IReadOnlyList<string>? Accounts,
    int? Limit);

/// <summary>
/// What the counterparty lookup tool returns: the counterparties whose name contains the text, every spelling merged under one
/// reference, so filters can be built for the other tools. Counterparty account numbers are masked and the household's own account
/// numbers never appear.
/// </summary>
public record CounterpartiesResult(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("counterparties")] IReadOnlyList<CounterpartyEntry> Counterparties,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("matching_total")] int MatchingTotal,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("limit_clamped")] bool LimitClamped,
    [property: JsonPropertyName("note")] string Note);

/// <summary>
/// One counterparty: the most frequent spelling as its name, up to five spellings that share the reference, the reference to pass
/// to the other tools, how many booked transactions it has, the first and last day and up to three masked account numbers.
/// </summary>
public record CounterpartyEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("counterparty_ref")] string CounterpartyRef,
    [property: JsonPropertyName("spellings")] IReadOnlyList<string> Spellings,
    [property: JsonPropertyName("transaction_count")] int TransactionCount,
    [property: JsonPropertyName("currencies")] IReadOnlyList<CounterpartyCurrency> Currencies,
    [property: JsonPropertyName("first_date")] string FirstDate,
    [property: JsonPropertyName("last_date")] string LastDate,
    [property: JsonPropertyName("accounts")] IReadOnlyList<string> Accounts);

/// <summary>The booked transactions of one counterparty in one currency. Money out is a non-negative magnitude.</summary>
public record CounterpartyCurrency(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("money_out")] string MoneyOut,
    [property: JsonPropertyName("money_in")] string MoneyIn);
