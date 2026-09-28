using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;

namespace Ledger.IntegrationTests.Database;

/// <summary>Proves each PostgreSQL role is confined to exactly its intended privileges, on real PostgreSQL.</summary>
[Collection("Database")]
public class DatabaseRoleTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Runtime_role_cannot_change_schema_but_can_use_its_own_tables()
    {
        await AssertDeniedAsync("ledger_runtime", "CREATE TABLE public.role_test_runtime_a (id int)", "42501");
        await AssertDeniedAsync(
            "ledger_runtime",
            "ALTER TABLE public.data_protection_canary ADD COLUMN role_test_column int",
            "42501");
        await AssertDeniedAsync("ledger_runtime", "DROP TABLE public.data_protection_canary", "42501");
        await AssertDeniedAsync("ledger_runtime", "CREATE SCHEMA role_test_schema", "42501");
        await AssertDeniedAsync(
            "ledger_runtime",
            "UPDATE public.\"__EFMigrationsHistory\" SET \"MigrationId\" = \"MigrationId\"",
            "42501");

        await AssertSucceedsAsync("ledger_runtime", "SELECT * FROM public.data_protection_keys");
        await AssertSucceedsAsync(
            "ledger_runtime",
            "INSERT INTO public.data_protection_keys (friendly_name, xml) VALUES ('role-test-runtime', '<xml/>')");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Grafana_reader_role_can_only_read_reporting()
    {
        await AssertDeniedAsync("grafana_reader", "SELECT * FROM public.data_protection_keys", "42501");
        await AssertDeniedAsync(
            "grafana_reader",
            "INSERT INTO public.data_protection_keys (friendly_name, xml) VALUES ('role-test-reader', '<xml/>')",
            "42501");
        await AssertDeniedAsync("grafana_reader", "CREATE TABLE reporting.role_test_reader (id int)", "42501");

        await ExecuteAsync("ledger_migrator", "CREATE VIEW reporting.role_test_view AS SELECT 1 AS value");
        await AssertSucceedsAsync("grafana_reader", "SELECT * FROM reporting.role_test_view");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Backup_role_can_read_everything_but_never_write()
    {
        await AssertSucceedsAsync("ledger_backup", "SELECT * FROM public.data_protection_keys");
        await AssertDeniedAsync(
            "ledger_backup",
            "INSERT INTO public.data_protection_keys (friendly_name, xml) VALUES ('role-test-backup', '<xml/>')",
            "42501");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task No_role_is_a_superuser_or_can_create_databases_or_roles()
    {
        foreach (var role in new[] { "ledger_runtime", "ledger_migrator", "grafana_reader", "ledger_backup" })
        {
            var attributes = await ReadRoleAttributesAsync(role);
            attributes.IsSuperuser.Should().BeFalse();
            attributes.CanCreateDatabase.Should().BeFalse();
            attributes.CanCreateRole.Should().BeFalse();
        }
    }

    private async Task AssertDeniedAsync(string role, string sql, string expectedSqlState)
    {
        var act = () => ExecuteAsync(role, sql);

        var assertion = await act.Should().ThrowAsync<PostgresException>();
        assertion.Which.SqlState.Should().Be(expectedSqlState);
    }

    private async Task AssertSucceedsAsync(string role, string sql)
    {
        var act = () => ExecuteAsync(role, sql);
        await act.Should().NotThrowAsync();
    }

    private async Task ExecuteAsync(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor(role));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(bool IsSuperuser, bool CanCreateDatabase, bool CanCreateRole)> ReadRoleAttributesAsync(string role)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = @role";
        command.Parameters.AddWithValue("role", role);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }
}
