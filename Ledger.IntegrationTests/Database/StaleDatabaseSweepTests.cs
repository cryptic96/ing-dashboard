using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;

namespace Ledger.IntegrationTests.Database;

/// <summary>Proves the sweep of leftover throwaway databases never touches a database that is current, in use or not its own.</summary>
[Collection("Database")]
public class StaleDatabaseSweepTests(DatabaseFixture fixture)
{
    [Fact]
    public void New_database_names_embed_the_creation_time_and_stay_within_the_identifier_limit()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 30, 45, TimeSpan.Zero);

        var name = DatabaseFixture.NewDatabaseName(now);

        name.Should().MatchRegex("^ledger_it_20261007123045_[0-9a-f]{16}$");
        name.Length.Should().BeLessThanOrEqualTo(63);
        DatabaseFixture.NewDatabaseName(now).Should().NotBe(name);
    }

    [Fact]
    public async Task The_sweep_drops_only_old_idle_throwaway_databases()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = DatabaseFixture.NewDatabaseName(now - TimeSpan.FromHours(3));
        var fresh = DatabaseFixture.NewDatabaseName(now - TimeSpan.FromMinutes(10));
        var staleButInUse = DatabaseFixture.NewDatabaseName(now - TimeSpan.FromHours(3));
        var legacyName = "ledger_it_" + Guid.NewGuid().ToString("N");
        var foreignName = "ledger_sweep_other_" + Guid.NewGuid().ToString("N")[..16];
        var untouchable = new[] { fresh, staleButInUse, legacyName, foreignName };

        foreach (var name in new[] { stale, fresh, staleButInUse })
        {
            fixture.RegisterForCleanup(name);
        }

        try
        {
            foreach (var name in new[] { stale, fresh, staleButInUse, legacyName, foreignName })
            {
                await CreateEmptyDatabaseAsync(name);
            }

            await using var inUse = new NpgsqlConnection(fixture.AdminConnectionStringFor(staleButInUse));
            await inUse.OpenAsync(TestContext.Current.CancellationToken);

            var dropped = await fixture.SweepStaleDatabasesAsync(now, DatabaseFixture.StaleDatabaseAge);

            dropped.Should().Contain(stale);
            dropped.Should().NotContain(untouchable);
            (await ExistingDatabasesAsync(stale)).Should().BeEmpty();
            (await ExistingDatabasesAsync(untouchable)).Should().BeEquivalentTo(untouchable);
        }
        finally
        {
            foreach (var name in new[] { legacyName, foreignName })
            {
                await DropOwnDatabaseAsync(name);
            }
        }
    }

    private async Task CreateEmptyDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task DropOwnDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> ExistingDatabasesAsync(params string[] names)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionStringFor("postgres"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT datname FROM pg_database WHERE datname = ANY(@names)";
        command.Parameters.AddWithValue("names", names);

        var existing = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            existing.Add(reader.GetString(0));
        }

        return existing;
    }
}
