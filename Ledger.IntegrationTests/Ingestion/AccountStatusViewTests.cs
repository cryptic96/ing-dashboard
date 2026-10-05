using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion.Synthetic;
using Npgsql;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Proves the account status view that the dashboards read: consent state and days left agree with the application's own
/// derivation, the balance shown is the closing booked balance of the latest day, unclear matches count only pending rows,
/// and nothing beyond the documented columns is exposed. Every test reads the view as the Grafana reader role.
/// </summary>
[Collection("Database")]
[Trait("Category", "Consent")]
public class AccountStatusViewTests(DatabaseFixture fixture)
{
    private static readonly string[] ExpectedColumns =
    [
        "account_key",
        "account_name",
        "last_success_at",
        "consent_state",
        "consent_days_left",
        "balance_amount",
        "balance_currency",
        "balance_date",
        "balance_reconciled",
        "flagged_count"
    ];

    [Theory]
    [InlineData(20161, "linked")]
    [InlineData(20159, "expiring")]
    [InlineData(1, "expiring")]
    [InlineData(-1, "expired")]
    [InlineData(-20000, "expired")]
    public async Task The_consent_state_equals_the_application_derivation_on_each_side_of_every_boundary(int minutesUntilValidUntil, string expectedState)
    {
        var seeded = await SeedAsync();
        await ExecuteAsMigratorAsync(
            "UPDATE bank_connections SET valid_until = now() + make_interval(mins => @minutes) WHERE id = @connectionId",
            ("minutes", minutesUntilValidUntil),
            ("connectionId", seeded.ConnectionId));

        var row = await ReadStatusAsync(seeded.AccountKey);

        row.ConsentState.Should().Be(expectedState);
        row.ConsentState.Should().Be(await DeriveAsync(seeded.ConnectionId));
    }

    [Theory]
    [InlineData("provider_expired", "expired")]
    [InlineData("revoked", "revoked")]
    [InlineData("superseded", "superseded")]
    public async Task A_stored_status_decides_the_consent_state_whatever_the_validity_end_is(string storedStatus, string expectedState)
    {
        var seeded = await SeedAsync();
        await ExecuteAsMigratorAsync(
            "UPDATE bank_connections SET status = @status, valid_until = now() + interval '80 days' WHERE id = @connectionId",
            ("status", storedStatus),
            ("connectionId", seeded.ConnectionId));

        var row = await ReadStatusAsync(seeded.AccountKey);

        row.ConsentState.Should().Be(expectedState);
        row.ConsentState.Should().Be(await DeriveAsync(seeded.ConnectionId));
    }

    [Fact]
    public async Task Consent_days_left_is_floored_and_never_below_zero()
    {
        var seeded = await SeedAsync();

        await ExecuteAsMigratorAsync(
            "UPDATE bank_connections SET valid_until = now() + interval '13 days 23 hours' WHERE id = @connectionId",
            ("connectionId", seeded.ConnectionId));
        (await ReadStatusAsync(seeded.AccountKey)).ConsentDaysLeft.Should().Be(13);

        await ExecuteAsMigratorAsync(
            "UPDATE bank_connections SET valid_until = now() - interval '3 days' WHERE id = @connectionId",
            ("connectionId", seeded.ConnectionId));
        (await ReadStatusAsync(seeded.AccountKey)).ConsentDaysLeft.Should().Be(0);
    }

    [Fact]
    public async Task The_last_success_is_the_latest_succeeded_run_and_ignores_failed_runs()
    {
        var seeded = await SeedAsync();
        var firstSuccess = (await ReadStatusAsync(seeded.AccountKey)).LastSuccessAt;
        firstSuccess.Should().NotBeNull();

        await ExecuteAsMigratorAsync(
            """
            INSERT INTO sync_runs (id, bank_connection_id, trigger, started_at, finished_at, outcome, calls_made, inserted, updated, dropped, flagged)
            VALUES (gen_random_uuid(), @connectionId, 'manual', now() + interval '1 hour', now() + interval '1 hour', 'failed_transient', 0, 0, 0, 0, 0)
            """,
            ("connectionId", seeded.ConnectionId));

        (await ReadStatusAsync(seeded.AccountKey)).LastSuccessAt.Should().Be(firstSuccess);
    }

    [Fact]
    public async Task The_closing_booked_balance_is_preferred_over_other_kinds_of_the_same_day()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "interim_available", 90.00m, reconciled: null);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "interim_booked", 80.00m, reconciled: null);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "closing_booked", 70.00m, reconciled: null);

        var row = await ReadStatusAsync(seeded.AccountKey);

        row.BalanceAmount.Should().Be(70.00m);
        row.BalanceCurrency.Should().Be("EUR");
        row.BalanceDate.Should().Be("2026-10-20");
    }

    [Fact]
    public async Task The_interim_booked_balance_is_preferred_over_an_available_balance_when_no_closing_balance_exists()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "interim_available", 90.00m, reconciled: null);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "interim_booked", 80.00m, reconciled: null);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceAmount.Should().Be(80.00m);
    }

    [Fact]
    public async Task A_later_dated_snapshot_wins_over_an_earlier_one_even_of_a_less_preferred_kind()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "closing_booked", 70.00m, reconciled: null);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 21), "interim_available", 65.00m, reconciled: null);

        var row = await ReadStatusAsync(seeded.AccountKey);

        row.BalanceAmount.Should().Be(65.00m);
        row.BalanceDate.Should().Be("2026-10-21");
    }

    [Theory]
    [InlineData(true, "yes")]
    [InlineData(false, "unknown")]
    [InlineData(null, "unknown")]
    public async Task The_reconciliation_verdict_of_a_single_checked_snapshot_is_reported_as_yes_or_unknown_never_as_no(bool? reconciled, string expected)
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "closing_booked", 70.00m, reconciled);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceReconciled.Should().Be(expected);
    }

    [Fact]
    public async Task A_mismatch_on_two_consecutive_checked_snapshots_of_the_same_kind_is_reported_as_no()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "expected", 70.00m, reconciled: false);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 21), "expected", 71.00m, reconciled: false);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceReconciled.Should().Be("no");
    }

    [Fact]
    public async Task A_mismatch_that_the_next_snapshot_resolves_is_never_reported_as_no()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "expected", 70.00m, reconciled: false);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceReconciled.Should().Be("unknown");

        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 21), "expected", 71.00m, reconciled: true);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceReconciled.Should().Be("yes");
    }

    [Fact]
    public async Task A_mismatch_after_a_match_is_a_first_mismatch_and_not_reported_as_no()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "expected", 70.00m, reconciled: false);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 21), "expected", 71.00m, reconciled: true);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 22), "expected", 72.00m, reconciled: false);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceReconciled.Should().Be("unknown");
    }

    [Fact]
    public async Task Snapshots_without_a_verdict_between_two_mismatches_do_not_break_the_consecutive_pair()
    {
        var seeded = await SeedAsync();
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 20), "expected", 70.00m, reconciled: false);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 21), "expected", 71.00m, reconciled: null);
        await InsertSnapshotAsync(seeded.AccountKey, new DateOnly(2026, 10, 22), "expected", 72.00m, reconciled: false);

        (await ReadStatusAsync(seeded.AccountKey)).BalanceReconciled.Should().Be("no");
    }

    [Fact]
    public async Task The_grafana_reader_can_select_the_view_and_has_no_write_privilege_on_it()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("grafana_reader"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT has_table_privilege('grafana_reader', 'reporting.account_status', 'SELECT'),
                   has_table_privilege('grafana_reader', 'reporting.account_status', 'INSERT')
                   OR has_table_privilege('grafana_reader', 'reporting.account_status', 'UPDATE')
                   OR has_table_privilege('grafana_reader', 'reporting.account_status', 'DELETE')
                   OR has_table_privilege('grafana_reader', 'reporting.account_status', 'TRUNCATE')
            """;

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue();
        reader.GetBoolean(1).Should().BeFalse();
    }

    [Fact]
    public async Task A_selected_account_without_a_snapshot_shows_an_empty_balance_and_unknown_reconciliation()
    {
        var seeded = await SeedAsync();

        var row = await ReadStatusAsync(seeded.AccountKey);

        row.BalanceAmount.Should().BeNull();
        row.BalanceCurrency.Should().BeNull();
        row.BalanceDate.Should().BeNull();
        row.BalanceReconciled.Should().Be("unknown");
        row.ConsentState.Should().Be("linked");
    }

    [Fact]
    public async Task An_account_that_was_not_selected_for_sync_does_not_appear()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        scenario.AddAccount(AccountKind.Savings);

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: true);

        (await ReadAllStatusAsync(connection.Accounts[0].AccountKey)).Should().HaveCount(1);
        (await ReadAllStatusAsync(connection.Accounts[1].AccountKey)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_pending_rows_with_an_unclear_match_are_counted()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scenario = SyntheticBankScenario.Create();
        var account = scenario.AddAccount(AccountKind.Current);
        scenario.AddTransaction(account, IngestionTestSupport.Pending("status-001", -3.10m, today, "Example Kiosk", "Snack"));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("status-002", -4.20m, today, "Example Cafe", "Coffee"));
        scenario.AddTransaction(account, IngestionTestSupport.Pending("status-003", -5.30m, today, "Example Bakery", "Bread"));
        scenario.AddTransaction(account, IngestionTestSupport.Booked("status-004", -6.40m, today.AddDays(-1), "Example Grocer", "Shopping"));

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        var accountKey = connection.Accounts[0].AccountKey;
        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        await ExecuteAsMigratorAsync(
            """
            UPDATE transactions SET match_flag = 'ambiguous'
            WHERE account_id = (SELECT id FROM accounts WHERE account_key = @accountKey)
              AND id IN (
                SELECT t.id FROM transactions t
                WHERE t.account_id = (SELECT id FROM accounts WHERE account_key = @accountKey)
                  AND t.status = 'pending'
                ORDER BY t.amount
                LIMIT 2)
            """,
            ("accountKey", accountKey));
        await ExecuteAsMigratorAsync(
            """
            UPDATE transactions SET match_flag = 'ambiguous'
            WHERE account_id = (SELECT id FROM accounts WHERE account_key = @accountKey) AND status = 'booked'
            """,
            ("accountKey", accountKey));

        (await ReadStatusAsync(accountKey)).FlaggedCount.Should().Be(2);
    }

    [Fact]
    public async Task The_view_exposes_exactly_the_documented_columns()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("grafana_reader"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'reporting' AND table_name = 'account_status'
            ORDER BY ordinal_position
            """;

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        columns.Should().Equal(ExpectedColumns);
    }

    private async Task<SeededAccount> SeedAsync()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);

        await using var factory = IngestionTestSupport.CreateFactory(fixture, scenario);
        var connection = await IngestionTestSupport.LinkSyntheticAsync(factory, scenario, selectFirstAccountOnly: false);
        await IngestionTestSupport.SyncAsync(factory, connection.Id);

        return new SeededAccount(connection.Id, connection.Accounts[0].AccountKey);
    }

    private async Task InsertSnapshotAsync(string accountKey, DateOnly snapshotDate, string kind, decimal amount, bool? reconciled)
    {
        var drift = reconciled == false ? 0.01m : 0m;

        await ExecuteAsMigratorAsync(
            """
            INSERT INTO balance_snapshots (id, account_id, snapshot_date, balance_kind, provider_type, amount, currency, expected_amount, drift_amount, reconciled, sync_run_id, created_at)
            SELECT gen_random_uuid(), a.id, @snapshotDate, @kind, 'TEST', @amount, 'EUR',
                   CASE WHEN @hasVerdict THEN @amount END,
                   CASE WHEN @hasVerdict THEN @drift END,
                   @reconciled,
                   (SELECT r.id FROM sync_runs r WHERE r.bank_connection_id = a.bank_connection_id LIMIT 1),
                   now()
            FROM accounts a
            WHERE a.account_key = @accountKey
            """,
            ("accountKey", accountKey),
            ("snapshotDate", snapshotDate),
            ("kind", kind),
            ("amount", amount),
            ("drift", drift),
            ("hasVerdict", reconciled.HasValue),
            ("reconciled", (object?)reconciled ?? DBNull.Value));
    }

    private async Task ExecuteAsMigratorAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_migrator"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> DeriveAsync(Guid connectionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("ledger_backup"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, valid_until, now() FROM public.bank_connections WHERE id = @connectionId";
        command.Parameters.AddWithValue("connectionId", connectionId);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        var status = reader.GetString(0) switch
        {
            "active" => ConnectionStatus.Active,
            "provider_expired" => ConnectionStatus.ProviderExpired,
            "revoked" => ConnectionStatus.Revoked,
            "superseded" => ConnectionStatus.Superseded,
            var other => throw new InvalidOperationException($"Unknown connection status {other}.")
        };

        var derived = ConsentState.Derive(
            status,
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2));

        return derived.State.ToString().ToLowerInvariant();
    }

    private async Task<StatusRow> ReadStatusAsync(string accountKey)
    {
        var rows = await ReadAllStatusAsync(accountKey);
        rows.Should().ContainSingle();
        return rows[0];
    }

    private async Task<IReadOnlyList<StatusRow>> ReadAllStatusAsync(string accountKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringFor("grafana_reader"));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_success_at, consent_state, consent_days_left, balance_amount, balance_currency,
                   balance_date, balance_reconciled, flagged_count
            FROM reporting.account_status
            WHERE account_key = @accountKey
            """;
        command.Parameters.AddWithValue("accountKey", accountKey);

        var rows = new List<StatusRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new StatusRow(
                reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7)));
        }

        return rows;
    }

    private sealed record SeededAccount(Guid ConnectionId, string AccountKey);

    private sealed record StatusRow(
        DateTimeOffset? LastSuccessAt,
        string ConsentState,
        int ConsentDaysLeft,
        decimal? BalanceAmount,
        string? BalanceCurrency,
        string? BalanceDate,
        string BalanceReconciled,
        int FlaggedCount);
}
