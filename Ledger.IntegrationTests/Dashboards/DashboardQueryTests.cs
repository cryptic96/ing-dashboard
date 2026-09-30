using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.IntegrationTests.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Npgsql;

namespace Ledger.IntegrationTests.Dashboards;

/// <summary>Runs every query of the committed dashboards against a migrated, synthetically seeded database as the Grafana reader role.</summary>
[Collection("Database")]
[Trait("Category", "Dashboards")]
public partial class DashboardQueryTests(DatabaseFixture fixture)
{
    private const string DashboardDirectory = "deploy/provisioning/grafana/provisioning/dashboards/json";

    private static readonly string[] DashboardFiles = ["ledger-sync-en.json", "ledger-sync-nl.json"];

    [GeneratedRegex(@"\$__timeFilter\([^)]*\)")]
    private static partial Regex TimeFilterMacro();

    [Fact]
    public async Task Every_committed_query_runs_as_the_grafana_reader_and_the_transactions_query_returns_rows()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Booked("dash-001", -21.50m, today.AddDays(-1), "Example Grocer", "Weekly shopping"));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("dash-002", -4.25m, today, "Example Bakery", "Bread"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;

        var result = await IngestionTestSupport.SyncAsync(factory, connection.Id);
        result.Inserted.Should().Be(2);

        await using var grafanaReader = new NpgsqlConnection(fixture.ConnectionStringFor("grafana_reader"));
        await grafanaReader.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var file in DashboardFiles)
        {
            var queries = ReadQueries(file).ToList();
            queries.Should().HaveCountGreaterThanOrEqualTo(2, file);

            foreach (var (kind, query) in queries)
            {
                var runnable = Expand(query, accountKey);
                var rows = await CountRowsAsync(grafanaReader, runnable);

                rows.Should().BeGreaterThan(0, $"{file} {kind} query should return the seeded data: {runnable}");
            }
        }
    }

    private static string Expand(string query, string accountKey)
    {
        var withoutTimeFilter = TimeFilterMacro().Replace(query, "TRUE");
        return withoutTimeFilter.Replace("${account:sqlstring}", $"'{accountKey}'", StringComparison.Ordinal);
    }

    private static async Task<int> CountRowsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
        }

        return rows;
    }

    private static IEnumerable<(string Kind, string Query)> ReadQueries(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), DashboardDirectory, fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var queries = new List<(string, string)>();

        foreach (var panel in document.RootElement.GetProperty("panels").EnumerateArray())
        {
            foreach (var target in panel.GetProperty("targets").EnumerateArray())
            {
                queries.Add(("panel", target.GetProperty("rawSql").GetString()!));
            }
        }

        foreach (var variable in document.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray())
        {
            queries.Add(("variable", variable.GetProperty("query").GetString()!));
        }

        return queries;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Ledger.slnx above the test output directory.");
    }
}
