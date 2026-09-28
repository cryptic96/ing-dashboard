using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;

namespace Ledger.IntegrationTests.Database;

/// <summary>Proves the packaged migration bundle applies migrations idempotently as the migrator role.</summary>
[Collection("Database")]
public class MigrationBundleTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "Migrations")]
    public async Task Packaged_bundle_migrates_a_bootstrapped_database_idempotently()
    {
        var bundlePath = Environment.GetEnvironmentVariable("LEDGER_EFBUNDLE");

        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
            {
                Assert.Fail("LEDGER_EFBUNDLE is required in CI.");
            }

            Assert.Skip("Set LEDGER_EFBUNDLE to a bundle built by build/package-release.sh first.");

            return;
        }

        var manifestPath = Path.Combine(Path.GetDirectoryName(bundlePath)!, "release-manifest.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        using var manifest = JsonDocument.Parse(manifestJson);
        var expectedMigrationIds = manifest.RootElement.GetProperty("migrations")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        var databaseName = await fixture.CreateBootstrappedDatabaseAsync();
        var migratorConnectionString = fixture.ConnectionStringForDatabase(databaseName, "ledger_migrator");

        var firstRunExitCode = await RunBundleAsync(bundlePath, migratorConnectionString);
        firstRunExitCode.Should().Be(0);

        var appliedMigrationIds = await ReadAppliedMigrationIdsAsync(databaseName);
        appliedMigrationIds.Should().BeEquivalentTo(expectedMigrationIds, options => options.WithStrictOrdering());

        var owners = await ReadTableOwnersAsync(databaseName);
        owners.Should().OnlyContain(owner => owner == "ledger_migrator");

        var secondRunExitCode = await RunBundleAsync(bundlePath, migratorConnectionString);
        secondRunExitCode.Should().Be(0);

        var appliedMigrationIdsAfterSecondRun = await ReadAppliedMigrationIdsAsync(databaseName);
        appliedMigrationIdsAfterSecondRun.Should().BeEquivalentTo(appliedMigrationIds, options => options.WithStrictOrdering());
    }

    private static async Task<int> RunBundleAsync(string bundlePath, string connectionString)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = bundlePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--connection");
        startInfo.ArgumentList.Add(connectionString);

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    private async Task<List<string?>> ReadAppliedMigrationIdsAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(databaseName, "ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";

        var ids = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<List<string>> ReadTableOwnersAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForDatabase(databaseName, "ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tableowner FROM pg_tables WHERE schemaname IN ('public', 'reporting')";

        var owners = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            owners.Add(reader.GetString(0));
        }

        return owners;
    }
}
