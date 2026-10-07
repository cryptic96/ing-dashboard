using System.ComponentModel;
using Ledger.Domain.Queries;
using Ledger.Service.Queries;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ledger.Service.Mcp;

/// <summary>The read-only tools Claude uses to answer questions about the household's money. Each tool only translates a call into a query.</summary>
[McpServerToolType]
public class LedgerTools(LedgerQueryService queries)
{
    /// <summary>Lists the synced accounts with balance, freshness and reach of history.</summary>
    [McpServerTool(Name = "ledger_overview", Title = "Ledger overview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Call this first. Lists the household's synced accounts by name with their latest balance, whether that balance "
        + "reconciles with the transactions, the last successful sync and how far booked history reaches, plus today's date "
        + "in Europe/Amsterdam. Use it to learn which accounts exist and how fresh the data is before answering anything else.")]
    public async Task<OverviewResult> LedgerOverview(CancellationToken cancellationToken)
    {
        McpMetrics.ToolCalled("ledger_overview");

        return await queries.OverviewAsync(cancellationToken);
    }

    /// <summary>Sums money out, money in and net over booked transactions for a period, with the filters, counts and exclusions stated.</summary>
    [McpServerTool(Name = "money_totals", Title = "Money totals", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Totals computed on the server from the household's booked transactions. Use it for every how-much or what-did-we-spend "
        + "question and never add amounts up yourself. Quote the period, the number of transactions and what was not included. "
        + "Pending transactions and transfers between the household's own accounts are reported beside the total, never in it, "
        + "and different currencies are never added together. Days, months and years follow the Europe/Amsterdam calendar. "
        + "Categories do not exist yet: pass the counterparty or description words that identify what is asked, for example the "
        + "supermarkets you mean, and say that the result is grouped by counterparty, not by category. The result lists the top "
        + "counterparties with a counterparty_ref you can pass back to focus on exactly one.")]
    public async Task<TotalsResult> MoneyTotals(
        [Description("A named period: today, yesterday, this_week, last_week, this_month, last_month, last_30_days, last_90_days, this_year or last_year. Use this or fromDate with toDate, not both.")]
        string? period = null,
        [Description("First day, inclusive, as yyyy-MM-dd. Use together with toDate instead of period.")]
        string? fromDate = null,
        [Description("Last day, inclusive, as yyyy-MM-dd. Use together with fromDate instead of period.")]
        string? toDate = null,
        [Description("Words that appear in the counterparty name, any one of them matches. Case-insensitive, up to 10 words of at most 100 characters.")]
        string[]? counterparty = null,
        [Description("counterparty_ref values from an earlier result, to focus on exactly those counterparties.")]
        string[]? counterpartyRef = null,
        [Description("Words that appear in the transaction description, any one of them matches. When counterparty and description are both given, a transaction must match both.")]
        string[]? description = null,
        [Description("account_key values from ledger_overview to narrow to; omit to include every synced account.")]
        string[]? accounts = null,
        [Description("out for money spent, in for money received, both for everything. Default both.")]
        string? direction = null,
        [Description("Smallest absolute amount to include, inclusive.")]
        decimal? minAmount = null,
        [Description("Largest absolute amount to include, inclusive.")]
        decimal? maxAmount = null,
        [Description("none, counterparty, month, week, day or account. Time groups include empty periods; day is limited to 92 days and week to 731 days.")]
        string? groupBy = null,
        CancellationToken cancellationToken = default)
    {
        McpMetrics.ToolCalled("money_totals");

        try
        {
            return await queries.TotalsAsync(
                new TotalsRequest(period, fromDate, toDate, counterparty, counterpartyRef, description, accounts, direction, minAmount, maxAmount, groupBy),
                cancellationToken);
        }
        catch (LedgerQueryException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    /// <summary>Shows detail rows of the transactions that match, newest first, capped and paged. Rows are never a source for totals.</summary>
    [McpServerTool(Name = "search_transactions", Title = "Search transactions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Shows supporting detail: the individual transactions that match, newest first. Results are capped at 100 rows a page and "
        + "always say how many rows matched and whether the page was cut short; pass next_cursor to continue. Never add the rows "
        + "up or use them to work out a total: use money_totals for every how-much question. Pending transactions are shown with "
        + "their status, and transfers between the household's own accounts are flagged internal_transfer. Counterparty account "
        + "numbers are masked. Days follow the Europe/Amsterdam calendar.")]
    public async Task<SearchResult> SearchTransactions(
        [Description("A named period: today, yesterday, this_week, last_week, this_month, last_month, last_30_days, last_90_days, this_year or last_year. Use this or fromDate with toDate, not both.")]
        string? period = null,
        [Description("First day, inclusive, as yyyy-MM-dd. Use together with toDate instead of period.")]
        string? fromDate = null,
        [Description("Last day, inclusive, as yyyy-MM-dd. Use together with fromDate instead of period.")]
        string? toDate = null,
        [Description("Words that appear in the counterparty name, any one of them matches. Case-insensitive, up to 10 words of at most 100 characters.")]
        string[]? counterparty = null,
        [Description("counterparty_ref values from an earlier result, to focus on exactly those counterparties.")]
        string[]? counterpartyRef = null,
        [Description("Words that appear in the transaction description, any one of them matches. When counterparty and description are both given, a transaction must match both.")]
        string[]? description = null,
        [Description("account_key values from ledger_overview to narrow to; omit to include every synced account.")]
        string[]? accounts = null,
        [Description("out for money spent, in for money received, both for everything. Default both.")]
        string? direction = null,
        [Description("Smallest absolute amount to include, inclusive.")]
        decimal? minAmount = null,
        [Description("Largest absolute amount to include, inclusive.")]
        decimal? maxAmount = null,
        [Description("booked, pending or both. Default both.")]
        string? status = null,
        [Description("Rows per page, 1 to 100. Default 50.")]
        int? limit = null,
        [Description("next_cursor from the previous page of exactly the same search; omit for the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        McpMetrics.ToolCalled("search_transactions");

        try
        {
            return await queries.SearchAsync(
                new SearchRequest(period, fromDate, toDate, counterparty, counterpartyRef, description, accounts, direction, minAmount, maxAmount, status, limit, cursor),
                cancellationToken);
        }
        catch (LedgerQueryException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
