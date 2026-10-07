using System.Text.Json.Serialization;

namespace Ledger.Service.Queries;

/// <summary>
/// What the ledger overview tool returns. Amounts are exact text with their currency, never numbers. No account number or other
/// bank identifier appears anywhere in it.
/// </summary>
public record OverviewResult(
    [property: JsonPropertyName("today")] string Today,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("accounts")] IReadOnlyList<OverviewAccount> Accounts,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note);

/// <summary>One synced account in the overview.</summary>
public record OverviewAccount(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("account_key")] string AccountKey,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("last_successful_sync")] string? LastSuccessfulSync,
    [property: JsonPropertyName("history_from")] string? HistoryFrom,
    [property: JsonPropertyName("history_to")] string? HistoryTo,
    [property: JsonPropertyName("pending_count")] int PendingCount,
    [property: JsonPropertyName("latest_balance")] OverviewBalance? LatestBalance,
    [property: JsonPropertyName("balance_reconciles")] string BalanceReconciles);

/// <summary>A balance as the bank reported it, with the date it applies to.</summary>
public record OverviewBalance(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("as_of")] string AsOf);
