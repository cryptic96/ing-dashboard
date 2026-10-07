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
        + "Say when a result is grouped by counterparty rather than by category.";
}
