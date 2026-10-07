using System.Reflection;
using ModelContextProtocol.Server;
using Prometheus;

namespace Ledger.Service.Mcp;

/// <summary>Counts MCP tool calls on /metrics. Every label is a fixed tool name, never anything a caller sent.</summary>
public static class McpMetrics
{
    private static readonly Counter ToolCalls = Prometheus.Metrics.CreateCounter(
        "ledger_mcp_tool_calls_total",
        "MCP tool calls received, by tool.",
        "tool");

    /// <summary>Creates the counter at zero for every tool the host serves, so a tool that was never called is still present.</summary>
    public static void InitialiseCounters()
    {
        foreach (var name in ToolNames())
        {
            ToolCalls.WithLabels(name);
        }
    }

    /// <summary>Counts one call of the named tool.</summary>
    /// <param name="tool">The tool's registered name.</param>
    public static void ToolCalled(string tool)
    {
        ToolCalls.WithLabels(tool).Inc();
    }

    private static IEnumerable<string> ToolNames()
    {
        return typeof(LedgerTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute?.Name is not null)
            .Select(attribute => attribute!.Name!);
    }
}
