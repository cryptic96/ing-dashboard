using FluentAssertions;
using Ledger.Service.Metrics;

namespace Ledger.UnitTests.Metrics;

/// <summary>Verifies how the build-info metric derives its version and commit labels.</summary>
public class LedgerMetricsTests
{
    [Fact]
    public void ParseInformationalVersion_splits_version_and_commit()
    {
        var (version, commit) = LedgerMetrics.ParseInformationalVersion(
            "0.1.0+375e41b7988b5691e16adb2f0726de3f73fab803");

        version.Should().Be("0.1.0");
        commit.Should().Be("375e41b7988b5691e16adb2f0726de3f73fab803");
    }

    [Fact]
    public void ParseInformationalVersion_reports_unknown_commit_without_a_suffix()
    {
        var (version, commit) = LedgerMetrics.ParseInformationalVersion("0.1.0");

        version.Should().Be("0.1.0");
        commit.Should().Be("unknown");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseInformationalVersion_reports_unknown_for_a_missing_version(string? informationalVersion)
    {
        var (version, commit) = LedgerMetrics.ParseInformationalVersion(informationalVersion);

        version.Should().Be("unknown");
        commit.Should().Be("unknown");
    }

    [Theory]
    [InlineData("0.1.0+", "0.1.0", "unknown")]
    [InlineData("+abc123", "unknown", "abc123")]
    public void ParseInformationalVersion_reports_unknown_for_an_empty_part(
        string informationalVersion, string expectedVersion, string expectedCommit)
    {
        var (version, commit) = LedgerMetrics.ParseInformationalVersion(informationalVersion);

        version.Should().Be(expectedVersion);
        commit.Should().Be(expectedCommit);
    }
}
