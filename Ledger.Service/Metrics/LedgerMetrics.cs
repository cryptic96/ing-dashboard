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
        var version = InformationalVersionWithoutSuffix(assembly);
        var commit = SourceRevisionId(assembly);

        BuildInfo.WithLabels(version, commit).Set(1);
    }

    /// <summary>Sets ledger_health_check_status{check} to 1 for Healthy, 0 otherwise.</summary>
    public static void RecordHealthCheckStatus(string checkName, bool isHealthy)
    {
        HealthCheckStatus.WithLabels(checkName).Set(isHealthy ? 1 : 0);
    }

    private static string InformationalVersionWithoutSuffix(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }

    private static string SourceRevisionId(Assembly assembly)
    {
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key == "SourceRevisionId" && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value;
            }
        }

        return "unknown";
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
