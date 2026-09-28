using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

namespace Ledger.Service.Metrics;

/// <summary>Registers the build-info gauge and the per-health-check status gauge exposed on /metrics.</summary>
public static class LedgerMetrics
{
    private static readonly Gauge BuildInfo = Prometheus.Metrics.CreateGauge(
        "ledger_build_info",
        "Always 1; labels carry the running build's version and commit.",
        "version", "commit");

    private static readonly Gauge HealthCheckStatus = Prometheus.Metrics.CreateGauge(
        "ledger_health_check_status",
        "1 when the named health check is healthy, 0 otherwise.",
        "check");

    /// <summary>Sets ledger_build_info to 1 with the running assembly's version and commit labels.</summary>
    public static void RecordBuildInfo(Assembly assembly)
    {
        var (version, commit) = ParseInformationalVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        BuildInfo.WithLabels(version, commit).Set(1);
    }

    /// <summary>Sets ledger_health_check_status{check} to 1 for Healthy, 0 otherwise.</summary>
    public static void RecordHealthCheckStatus(string checkName, bool isHealthy)
    {
        HealthCheckStatus.WithLabels(checkName).Set(isHealthy ? 1 : 0);
    }

    /// <summary>
    /// Splits an informational version such as "1.2.3+{commit}" into its version and commit.
    /// MSBuild appends the SourceRevisionId property after a '+'; either part is "unknown" when absent.
    /// </summary>
    public static (string Version, string Commit) ParseInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return ("unknown", "unknown");
        }

        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0)
        {
            return (informationalVersion, "unknown");
        }

        var version = plusIndex == 0 ? "unknown" : informationalVersion[..plusIndex];
        var commit = plusIndex == informationalVersion.Length - 1 ? "unknown" : informationalVersion[(plusIndex + 1)..];
        return (version, commit);
    }
}

/// <summary>Publishes every health check result as a Prometheus gauge after each health-check run.</summary>
public class HealthCheckMetricsPublisher : IHealthCheckPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in report.Entries)
        {
            LedgerMetrics.RecordHealthCheckStatus(name, entry.Status == HealthStatus.Healthy);
        }

        return Task.CompletedTask;
    }
}
