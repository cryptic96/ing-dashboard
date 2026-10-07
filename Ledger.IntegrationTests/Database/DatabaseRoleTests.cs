using FluentAssertions;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;

namespace Ledger.IntegrationTests.Database;

/// <summary>
/// Proves each PostgreSQL role is confined to exactly its intended privileges, on real PostgreSQL. The checks enumerate the
/// catalog instead of sampling tables, so a relation, sequence, function or schema added later cannot slip in with broad
/// default privileges unnoticed. Roles are exercised through the admin connection with the role switched, which proves what
/// each role may do; that each can authenticate is covered by its login attribute here and by the host's connection rules.
/// </summary>
[Collection("Database")]
public class DatabaseRoleTests(DatabaseFixture fixture)
{
    private static readonly string[] ApplicationRoles = ["ledger_runtime", "ledger_migrator", "grafana_reader", "ledger_backup"];

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
    public async Task Runtime_role_cannot_rewrite_history_or_delete_ledger_rows()
    {
        await AssertDeniedAsync("ledger_runtime", "UPDATE public.transaction_payloads SET observed_at = observed_at", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.transaction_payloads", "42501");
        await AssertDeniedAsync("ledger_runtime", "TRUNCATE public.transaction_payloads", "42501");
        await AssertDeniedAsync("ledger_runtime", "UPDATE public.transaction_refs SET transaction_id = transaction_id", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.transaction_refs", "42501");
        await AssertDeniedAsync("ledger_runtime", "TRUNCATE public.transaction_refs", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.transactions", "42501");
        await AssertDeniedAsync("ledger_runtime", "TRUNCATE public.transactions", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.accounts", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.bank_connections", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.sync_runs", "42501");

        await AssertDeniedAsync("ledger_runtime", "UPDATE public.transactions SET id = id", "42501");
        await AssertDeniedAsync("ledger_runtime", "UPDATE public.transactions SET account_id = account_id", "42501");
        await AssertDeniedAsync("ledger_runtime", "UPDATE public.transactions SET first_seen_at = first_seen_at", "42501");

        await AssertSucceedsAsync("ledger_runtime", "UPDATE public.transactions SET status = status");
        await AssertSucceedsAsync("ledger_runtime", "UPDATE public.accounts SET display_name = display_name");
        await AssertSucceedsAsync("ledger_runtime", "UPDATE public.sync_runs SET outcome = outcome");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Runtime_role_can_only_append_to_the_provider_call_ledger()
    {
        await AssertDeniedAsync("ledger_runtime", "UPDATE public.provider_calls SET background = background", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.provider_calls", "42501");
        await AssertDeniedAsync("ledger_runtime", "TRUNCATE public.provider_calls", "42501");

        var granted = await QueryNamesAsync("""
            SELECT 'insert' WHERE has_table_privilege('ledger_runtime', 'public.provider_calls', 'INSERT')
            UNION ALL
            SELECT 'select' WHERE has_table_privilege('ledger_runtime', 'public.provider_calls', 'SELECT')
            """);
        granted.Should().BeEquivalentTo(["insert", "select"]);
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Runtime_role_can_only_append_to_the_balance_snapshots()
    {
        await AssertDeniedAsync("ledger_runtime", "UPDATE public.balance_snapshots SET amount = amount", "42501");
        await AssertDeniedAsync("ledger_runtime", "DELETE FROM public.balance_snapshots", "42501");
        await AssertDeniedAsync("ledger_runtime", "TRUNCATE public.balance_snapshots", "42501");

        var granted = await QueryNamesAsync("""
            SELECT 'insert' WHERE has_table_privilege('ledger_runtime', 'public.balance_snapshots', 'INSERT')
            UNION ALL
            SELECT 'select' WHERE has_table_privilege('ledger_runtime', 'public.balance_snapshots', 'SELECT')
            """);
        granted.Should().BeEquivalentTo(["insert", "select"]);
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Grafana_reader_has_only_select_on_every_reporting_object_and_nothing_in_public()
    {
        var writable = await QueryNamesAsync("""
            SELECT c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'reporting' AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
              AND (has_table_privilege('grafana_reader', c.oid, 'INSERT')
                OR has_table_privilege('grafana_reader', c.oid, 'UPDATE')
                OR has_table_privilege('grafana_reader', c.oid, 'DELETE')
                OR has_table_privilege('grafana_reader', c.oid, 'TRUNCATE')
                OR has_table_privilege('grafana_reader', c.oid, 'REFERENCES')
                OR has_table_privilege('grafana_reader', c.oid, 'TRIGGER'))
            """);
        writable.Should().BeEmpty();

        var unreadable = await QueryNamesAsync("""
            SELECT c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'reporting' AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
              AND NOT has_table_privilege('grafana_reader', c.oid, 'SELECT')
            """);
        unreadable.Should().BeEmpty();

        var reportingObjects = await QueryNamesAsync("""
            SELECT c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'reporting' AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
            """);
        reportingObjects.Should().Contain(["accounts", "transactions"]);

        var publicAccess = await QueryNamesAsync("""
            SELECT 'schema' WHERE has_schema_privilege('grafana_reader', 'public', 'USAGE')
            UNION ALL
            SELECT c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
              AND (has_table_privilege('grafana_reader', c.oid, 'SELECT')
                OR has_table_privilege('grafana_reader', c.oid, 'INSERT')
                OR has_table_privilege('grafana_reader', c.oid, 'UPDATE')
                OR has_table_privilege('grafana_reader', c.oid, 'DELETE'))
            """);
        publicAccess.Should().BeEmpty();

        await AssertDeniedAsync("grafana_reader", "SELECT * FROM public.transactions", "42501");
        await AssertDeniedAsync("grafana_reader", "SELECT * FROM public.transaction_payloads", "42501");
        await AssertDeniedAsync("grafana_reader", "DELETE FROM public.transactions", "42501");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Backup_role_can_read_every_relation_and_never_write_any()
    {
        await AssertSucceedsAsync("ledger_backup", "SELECT * FROM public.data_protection_keys");
        await AssertDeniedAsync(
            "ledger_backup",
            "INSERT INTO public.data_protection_keys (friendly_name, xml) VALUES ('role-test-backup', '<xml/>')",
            "42501");

        var matrix = await PrivilegeMatrixAsync("ledger_backup");

        matrix.Should().HaveCountGreaterThan(10);
        matrix.Should().OnlyContain(line => line.EndsWith("=SELECT", StringComparison.Ordinal));

        await AssertNoSequenceWriteAsync("ledger_backup");
        await AssertNoCreationRightsAsync("ledger_backup");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Runtime_role_holds_exactly_the_intended_privileges_on_every_relation_and_sequence()
    {
        var matrix = await PrivilegeMatrixAsync("ledger_runtime");

        matrix.Should().BeEquivalentTo(
        [
            "public.__EFMigrationsHistory=SELECT",
            "public.accounts=SELECT,INSERT,UPDATE",
            "public.api_keys=SELECT,INSERT,UPDATE,DELETE",
            "public.balance_snapshots=SELECT,INSERT",
            "public.bank_authorizations=SELECT,INSERT,UPDATE,DELETE",
            "public.bank_connections=SELECT,INSERT,UPDATE",
            "public.data_protection_canary=SELECT,INSERT,UPDATE,DELETE",
            "public.data_protection_keys=SELECT,INSERT,UPDATE,DELETE",
            "public.provider_calls=SELECT,INSERT",
            "public.sync_runs=SELECT,INSERT,UPDATE",
            "public.transaction_payloads=SELECT,INSERT",
            "public.transaction_refs=SELECT,INSERT",
            "public.transactions=SELECT,INSERT",
            "reporting.account_status=",
            "reporting.accounts=",
            "reporting.transactions="
        ],
        "a relation that is missing here is new, and its privileges for the runtime role must be decided on purpose");

        (await QueryNamesAsync("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('public', 'reporting') AND c.relkind = 'S'
              AND NOT CASE WHEN c.relkind = 'S'
                  THEN has_sequence_privilege('ledger_runtime', c.oid, 'USAGE')
                   AND has_sequence_privilege('ledger_runtime', c.oid, 'SELECT')
                   AND NOT has_sequence_privilege('ledger_runtime', c.oid, 'UPDATE')
                  END
            """)).Should().BeEmpty();

        await AssertNoCreationRightsAsync("ledger_runtime");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Runtime_role_can_update_only_the_mutable_columns_of_the_transactions_table()
    {
        var updatable = await QueryNamesAsync("""
            SELECT c.relname || '.' || a.attname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
              AND NOT has_table_privilege('ledger_runtime', c.oid, 'UPDATE')
              AND has_column_privilege('ledger_runtime', c.oid, a.attnum, 'UPDATE')
            """);

        updatable.Should().BeEquivalentTo(
            new[]
            {
                "status", "booking_date", "value_date", "transaction_date", "amount", "counterparty_name",
                "counterparty_iban", "description", "match_flag", "updated_at", "booked_at", "dropped_at"
            }.Select(column => "transactions." + column));
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task The_database_holds_only_the_expected_schemas_and_no_application_functions()
    {
        (await QueryNamesAsync("""
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
            """)).Should().BeEquivalentTo(["public", "reporting"]);

        (await QueryNamesAsync("""
            SELECT n.nspname || '.' || p.proname
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname IN ('public', 'reporting')
            """)).Should().BeEmpty("a function that every role can execute by default must be reviewed before it is added");
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Every_application_role_is_an_unprivileged_login_with_only_the_intended_memberships()
    {
        foreach (var role in ApplicationRoles)
        {
            var attributes = await ReadRoleAttributesAsync(role);
            attributes.Should().Be(
                new RoleAttributes(
                    IsSuperuser: false,
                    CanCreateDatabase: false,
                    CanCreateRole: false,
                    CanReplicate: false,
                    BypassesRowSecurity: false,
                    CanLogin: true),
                $"role {role} must be an ordinary login without elevated attributes");
        }

        (await QueryNamesAsync("""
            SELECT member.rolname || ' -> ' || parent.rolname
            FROM pg_auth_members membership
            JOIN pg_roles member ON member.oid = membership.member
            JOIN pg_roles parent ON parent.oid = membership.roleid
            WHERE member.rolname IN ('ledger_runtime', 'ledger_migrator', 'grafana_reader', 'ledger_backup')
            """)).Should().BeEquivalentTo(["ledger_backup -> pg_read_all_data"]);

        foreach (var role in ApplicationRoles)
        {
            foreach (var other in ApplicationRoles.Where(candidate => candidate != role))
            {
                (await QueryNamesAsync(
                    "SELECT 'member' WHERE pg_has_role(@role, @other, 'MEMBER')",
                    ("role", role),
                    ("other", other))).Should().BeEmpty($"{role} must not inherit the rights of {other}");
            }

            foreach (var predefined in new[] { "pg_write_all_data", "pg_execute_server_program", "pg_read_server_files", "pg_write_server_files", "pg_signal_backend" })
            {
                (await QueryNamesAsync(
                    "SELECT 'member' WHERE pg_has_role(@role, @predefined, 'MEMBER')",
                    ("role", role),
                    ("predefined", predefined))).Should().BeEmpty($"{role} must not be a member of {predefined}");
            }
        }
    }

    [Fact]
    [Trait("Category", "DatabaseRoles")]
    public async Task Connecting_is_granted_to_the_application_roles_only_and_never_to_public()
    {
        (await QueryNamesAsync("""
            SELECT CASE WHEN g.grantee = 0 THEN 'PUBLIC' ELSE g.grantee::regrole::text END || ' ' || g.privilege_type
            FROM pg_database d, aclexplode(d.datacl) g
            WHERE d.datname = current_database() AND g.grantee <> d.datdba
            """)).Should().BeEquivalentTo(
            [
                "ledger_runtime CONNECT",
                "grafana_reader CONNECT",
                "ledger_backup CONNECT"
            ]);
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

    private async Task<List<string>> PrivilegeMatrixAsync(string role)
    {
        return await QueryNamesAsync(
            """
            SELECT n.nspname || '.' || c.relname || '=' || concat_ws(',',
                CASE WHEN has_table_privilege(@role, c.oid, 'SELECT') THEN 'SELECT' END,
                CASE WHEN has_table_privilege(@role, c.oid, 'INSERT') THEN 'INSERT' END,
                CASE WHEN has_table_privilege(@role, c.oid, 'UPDATE') THEN 'UPDATE' END,
                CASE WHEN has_table_privilege(@role, c.oid, 'DELETE') THEN 'DELETE' END,
                CASE WHEN has_table_privilege(@role, c.oid, 'TRUNCATE') THEN 'TRUNCATE' END,
                CASE WHEN has_table_privilege(@role, c.oid, 'REFERENCES') THEN 'REFERENCES' END,
                CASE WHEN has_table_privilege(@role, c.oid, 'TRIGGER') THEN 'TRIGGER' END)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('public', 'reporting') AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
            """,
            ("role", role));
    }

    private async Task AssertNoSequenceWriteAsync(string role)
    {
        (await QueryNamesAsync(
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('public', 'reporting') AND c.relkind = 'S'
              AND CASE WHEN c.relkind = 'S' THEN has_sequence_privilege(@role, c.oid, 'UPDATE') END
            """,
            ("role", role))).Should().BeEmpty();
    }

    private async Task AssertNoCreationRightsAsync(string role)
    {
        (await QueryNamesAsync(
            """
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
              AND has_schema_privilege(@role, oid, 'CREATE')
            UNION ALL
            SELECT 'database ' || privilege FROM unnest(ARRAY['CREATE', 'TEMPORARY']) AS privilege
            WHERE has_database_privilege(@role, current_database(), privilege)
            """,
            ("role", role))).Should().BeEmpty($"{role} must not create anything");
    }

    private async Task<List<string>> QueryNamesAsync(string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<RoleAttributes> ReadRoleAttributesAsync(string role)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin
            FROM pg_roles WHERE rolname = @role
            """;
        command.Parameters.AddWithValue("role", role);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"role {role} must exist");

        return new RoleAttributes(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5));
    }

    private sealed record RoleAttributes(
        bool IsSuperuser,
        bool CanCreateDatabase,
        bool CanCreateRole,
        bool CanReplicate,
        bool BypassesRowSecurity,
        bool CanLogin);
}
