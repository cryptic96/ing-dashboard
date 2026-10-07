using System.ComponentModel;
using Ledger.Service.Queries;
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
}
