using System.Reflection;
using ModelContextProtocol.Server;
using Prometheus;

namespace Ledger.Service.Mcp;

/// <summary>
/// Counts MCP tool calls, rejected access tokens, approved connections and reused refresh tokens on /metrics. Every label is a
/// fixed tool name or a fixed reason word, never anything a caller sent: no token, client, address or login name.
/// </summary>
public static class McpMetrics
{
    /// <summary>The access token had passed its lifetime.</summary>
    public const string ReasonExpired = "expired";

    /// <summary>The access token was issued for a different resource than this server.</summary>
    public const string ReasonWrongAudience = "wrong_audience";

    /// <summary>The access token was malformed, unknown, revoked or otherwise unusable.</summary>
    public const string ReasonInvalid = "invalid";

    /// <summary>Every reason a rejected token can be counted under.</summary>
    public static readonly IReadOnlyList<string> RejectionReasons = [ReasonExpired, ReasonWrongAudience, ReasonInvalid];

    private static readonly Counter ToolCalls = Prometheus.Metrics.CreateCounter(
        "ledger_mcp_tool_calls_total",
        "MCP tool calls received, by tool.",
        "tool");

    private static readonly Counter RejectedTokens = Prometheus.Metrics.CreateCounter(
        "ledger_mcp_rejected_tokens_total",
        "Access tokens presented to the MCP endpoint and rejected, by reason: expired, wrong_audience or invalid.",
        "reason");

    private static readonly Counter GrantsCreated = Prometheus.Metrics.CreateCounter(
        "ledger_oauth_grants_created_total",
        "Claude connections approved on the consent page.");

    private static readonly Counter RefreshTokenReuse = Prometheus.Metrics.CreateCounter(
        "ledger_oauth_refresh_token_reuse_total",
        "Refresh tokens presented again after they had already been used, which revokes the tokens of that connection.");

    /// <summary>
    /// Creates a counter at zero for every tool the host serves and for every rejection reason, so nothing is absent before its
    /// first event. The two counters without labels already start at zero.
    /// </summary>
    public static void InitialiseCounters()
    {
        foreach (var name in ToolNames())
        {
            ToolCalls.WithLabels(name);
        }

        foreach (var reason in RejectionReasons)
        {
            RejectedTokens.WithLabels(reason);
        }
    }

    /// <summary>Counts one call of the named tool.</summary>
    /// <param name="tool">The tool's registered name.</param>
    public static void ToolCalled(string tool)
    {
        ToolCalls.WithLabels(tool).Inc();
    }

    /// <summary>Counts one rejected access token.</summary>
    /// <param name="reason">One of <see cref="RejectionReasons"/>; anything else is counted as invalid so the label set stays fixed.</param>
    public static void TokenRejected(string reason)
    {
        var label = RejectionReasons.Contains(reason, StringComparer.Ordinal) ? reason : ReasonInvalid;

        RejectedTokens.WithLabels(label).Inc();
    }

    /// <summary>Counts one approved Claude connection.</summary>
    public static void GrantCreated()
    {
        GrantsCreated.Inc();
    }

    /// <summary>Counts one refresh token that was presented after it had already been used.</summary>
    public static void RefreshTokenReused()
    {
        RefreshTokenReuse.Inc();
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
