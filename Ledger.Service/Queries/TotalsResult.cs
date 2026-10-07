using System.Text.Json.Serialization;

namespace Ledger.Service.Queries;

/// <summary>
/// What the money totals tool is asked. Every field is optional text or a number chosen by Claude and is validated before use.
/// Dates are calendar dates written as yyyy-MM-dd.
/// </summary>
public record TotalsRequest(
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
    string? GroupBy);

/// <summary>
/// What the money totals tool returns. Every amount is exact text with its currency, never a number, and every total is a server
/// sum over booked transactions only. No account number or other bank identifier appears anywhere in it.
/// </summary>
public record TotalsResult(
    [property: JsonPropertyName("period")] TotalsPeriod Period,
    [property: JsonPropertyName("filters")] TotalsFilters Filters,
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("currencies")] IReadOnlyList<TotalsCurrency> Currencies,
    [property: JsonPropertyName("pending_not_included")] IReadOnlyList<TotalsExcluded> PendingNotIncluded,
    [property: JsonPropertyName("internal_transfers_excluded")] IReadOnlyList<TotalsExcluded> InternalTransfersExcluded,
    [property: JsonPropertyName("grouped_by")] string GroupedBy,
    [property: JsonPropertyName("data_as_of")] IReadOnlyList<TotalsDataAsOf> DataAsOf,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>The days a result covers, resolved to absolute dates, with the keyword that was asked for.</summary>
public record TotalsPeriod(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("requested")] string Requested,
    [property: JsonPropertyName("time_zone")] string TimeZone);

/// <summary>The filters that were applied, normalised, so a total can be reproduced.</summary>
public record TotalsFilters(
    [property: JsonPropertyName("counterparty")] IReadOnlyList<string> Counterparty,
    [property: JsonPropertyName("counterparty_ref")] IReadOnlyList<string> CounterpartyRef,
    [property: JsonPropertyName("description")] IReadOnlyList<string> Description,
    [property: JsonPropertyName("accounts")] IReadOnlyList<string> Accounts,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("min_amount")] string? MinAmount,
    [property: JsonPropertyName("max_amount")] string? MaxAmount,
    [property: JsonPropertyName("group_by")] string GroupBy);

/// <summary>The totals of one currency. Money out is a non-negative magnitude and net is money in minus money out.</summary>
public record TotalsCurrency(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("money_out")] string MoneyOut,
    [property: JsonPropertyName("money_in")] string MoneyIn,
    [property: JsonPropertyName("net")] string Net,
    [property: JsonPropertyName("transaction_count")] int TransactionCount,
    [property: JsonPropertyName("counterparty_count")] int CounterpartyCount,
    [property: JsonPropertyName("counterparties")] IReadOnlyList<TotalsCounterparty> Counterparties,
    [property: JsonPropertyName("groups")] IReadOnlyList<TotalsGroup> Groups);

/// <summary>One counterparty of a breakdown, or the remainder row that merges the rest.</summary>
public record TotalsCounterparty(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("counterparty_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CounterpartyRef,
    [property: JsonPropertyName("money_out")] string MoneyOut,
    [property: JsonPropertyName("money_in")] string MoneyIn,
    [property: JsonPropertyName("transaction_count")] int TransactionCount,
    [property: JsonPropertyName("merged_counterparties"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MergedCounterparties);

/// <summary>One group of a grouped result: a period, an account or a counterparty.</summary>
public record TotalsGroup(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? From,
    [property: JsonPropertyName("to"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? To,
    [property: JsonPropertyName("account_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccountKey,
    [property: JsonPropertyName("counterparty_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CounterpartyRef,
    [property: JsonPropertyName("money_out")] string MoneyOut,
    [property: JsonPropertyName("money_in")] string MoneyIn,
    [property: JsonPropertyName("net")] string Net,
    [property: JsonPropertyName("transaction_count")] int TransactionCount,
    [property: JsonPropertyName("merged_counterparties"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MergedCounterparties);

/// <summary>Transactions that were left out of a total for one currency: how many, and how much went out and came in.</summary>
public record TotalsExcluded(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("transaction_count")] int TransactionCount,
    [property: JsonPropertyName("money_out")] string MoneyOut,
    [property: JsonPropertyName("money_in")] string MoneyIn);

/// <summary>When one account in scope last synced successfully. The time is null when it never has.</summary>
public record TotalsDataAsOf(
    [property: JsonPropertyName("account_key")] string AccountKey,
    [property: JsonPropertyName("account_name")] string AccountName,
    [property: JsonPropertyName("last_successful_sync")] string? LastSuccessfulSync);
