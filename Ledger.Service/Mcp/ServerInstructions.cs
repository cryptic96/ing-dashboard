namespace Ledger.Service.Mcp;

/// <summary>The instructions every MCP client receives when it connects, telling Claude how to use the tools.</summary>
public static class ServerInstructions
{
    /// <summary>The instruction text.</summary>
    public const string Text =
        "Read-only access to a household's bank ledger. "
        + "Call ledger_overview first: it shows which accounts are synced, how fresh the data is and how far history reaches. "
        + "Days, months and years follow the Europe/Amsterdam calendar. "
        + "For any how-much question use money_totals and never add amounts up yourself. "
        + "Quote the period, the number of transactions and what was excluded. "
        + "Until categories exist, totals are grouped by counterparty name, not by category: say so in the answer, and use "
        + "find_counterparties to look up how a merchant is spelled before filtering by it. "
        + "search_transactions shows capped detail rows only; never add its rows up.";
}
