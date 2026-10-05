using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerIngestion : Migration
    {
        private const string MutableTransactionColumnsGrant =
            "GRANT UPDATE (status, booking_date, value_date, transaction_date, amount, counterparty_name, counterparty_iban, description, match_flag, updated_at, booked_at, dropped_at) ON TABLE transactions TO ledger_runtime;";

        private const string ReportingAccountsView = """
            CREATE VIEW reporting.accounts AS
            SELECT
                a.account_key,
                COALESCE(a.display_name, a.account_key) AS display_name,
                a.kind,
                a.currency,
                a.sync_enabled
            FROM public.accounts a
            WHERE a.sync_enabled
               OR EXISTS (SELECT 1 FROM public.transactions t WHERE t.account_id = a.id);
            """;

        private const string ReportingTransactionsView = """
            CREATE VIEW reporting.transactions AS
            SELECT
                t.id AS transaction_id,
                a.account_key,
                COALESCE(a.display_name, a.account_key) AS account_name,
                to_char(COALESCE(t.booking_date, t.transaction_date, t.value_date), 'YYYY-MM-DD') AS effective_date,
                (COALESCE(t.booking_date, t.transaction_date, t.value_date)::timestamp AT TIME ZONE 'Europe/Amsterdam') AS effective_at,
                to_char(t.booking_date, 'YYYY-MM-DD') AS booking_date,
                to_char(t.value_date, 'YYYY-MM-DD') AS value_date,
                t.amount,
                t.currency,
                t.counterparty_name,
                t.description,
                t.status,
                t.match_flag,
                t.first_seen_at
            FROM public.transactions t
            JOIN public.accounts a ON a.id = t.account_id
            WHERE t.status <> 'dropped'
            ORDER BY COALESCE(t.booking_date, t.transaction_date, t.value_date) DESC, t.first_seen_at DESC, t.id DESC;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    aspsp_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aspsp_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    session_id_protected = table.Column<string>(type: "text", nullable: false),
                    authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_connections", x => x.id);
                    table.CheckConstraint("ck_bank_connections_status", "status IN ('active', 'provider_expired', 'revoked', 'superseded')");
                    table.ForeignKey(
                        name: "fk_bank_connections_bank_connections_superseded_by_id",
                        column: x => x.superseded_by_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_account_uid = table.Column<string>(type: "text", nullable: true),
                    identification_hash = table.Column<string>(type: "text", nullable: false),
                    iban = table.Column<string>(type: "text", nullable: true),
                    provider_name = table.Column<string>(type: "text", nullable: true),
                    product = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    sync_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_kind", "kind IN ('current', 'savings', 'card', 'other')");
                    table.ForeignKey(
                        name: "fk_accounts_bank_connections_bank_connection_id",
                        column: x => x.bank_connection_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    provider_error = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    calls_made = table.Column<int>(type: "integer", nullable: false),
                    inserted = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    dropped = table.Column<int>(type: "integer", nullable: false),
                    flagged = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_runs", x => x.id);
                    table.CheckConstraint("ck_sync_runs_outcome", "outcome IS NULL OR outcome IN ('succeeded', 'failed_transient', 'failed_rate_limited', 'failed_consent', 'failed_provider_auth', 'failed_malformed', 'quota_exhausted', 'abandoned')");
                    table.CheckConstraint("ck_sync_runs_trigger", "trigger IN ('scheduled', 'retry', 'post_link', 'manual')");
                    table.ForeignKey(
                        name: "fk_sync_runs_bank_connections_bank_connection_id",
                        column: x => x.bank_connection_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    booking_date = table.Column<DateOnly>(type: "date", nullable: true),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    transaction_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    counterparty_name = table.Column<string>(type: "text", nullable: true),
                    counterparty_iban = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    match_flag = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    booked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dropped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_transactions_match_flag", "match_flag IS NULL OR match_flag IN ('ambiguous')");
                    table.CheckConstraint("ck_transactions_status", "status IN ('pending', 'booked', 'dropped')");
                    table.ForeignKey(
                        name: "fk_transactions_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transaction_payloads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_sha256 = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction_payloads", x => x.id);
                    table.ForeignKey(
                        name: "fk_transaction_payloads_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_payloads_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transaction_refs",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_status = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction_refs", x => new { x.account_id, x.@ref });
                    table.CheckConstraint("ck_transaction_refs_first_status", "first_status IN ('pending', 'booked', 'dropped')");
                    table.ForeignKey(
                        name: "fk_transaction_refs_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_refs_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_account_key",
                table: "accounts",
                column: "account_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_bank_connection_id",
                table: "accounts",
                column: "bank_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounts_provider_identification_hash",
                table: "accounts",
                columns: new[] { "provider", "identification_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_connections_connection_key",
                table: "bank_connections",
                column: "connection_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_connections_superseded_by_id",
                table: "bank_connections",
                column: "superseded_by_id");

            migrationBuilder.CreateIndex(
                name: "ux_sync_runs_unfinished_per_connection",
                table: "sync_runs",
                column: "bank_connection_id",
                unique: true,
                filter: "finished_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_payloads_sync_run_id",
                table: "transaction_payloads",
                column: "sync_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_payloads_transaction_id_observed_at",
                table: "transaction_payloads",
                columns: new[] { "transaction_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_refs_transaction_id",
                table: "transaction_refs",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_account_id_booking_date",
                table: "transactions",
                columns: new[] { "account_id", "booking_date" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_account_id_status",
                table: "transactions",
                columns: new[] { "account_id", "status" });

            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE, TRUNCATE ON TABLE transaction_payloads FROM ledger_runtime;");
            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE, TRUNCATE ON TABLE transaction_refs FROM ledger_runtime;");
            migrationBuilder.Sql(
                "REVOKE DELETE, TRUNCATE ON TABLE transactions, accounts, bank_connections, sync_runs FROM ledger_runtime;");
            migrationBuilder.Sql("REVOKE UPDATE ON TABLE transactions FROM ledger_runtime;");
            migrationBuilder.Sql(MutableTransactionColumnsGrant);

            migrationBuilder.Sql(ReportingAccountsView);
            migrationBuilder.Sql(ReportingTransactionsView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW reporting.transactions;");
            migrationBuilder.Sql("DROP VIEW reporting.accounts;");

            migrationBuilder.Sql(
                "REVOKE UPDATE (status, booking_date, value_date, transaction_date, amount, counterparty_name, counterparty_iban, description, match_flag, updated_at, booked_at, dropped_at) ON TABLE transactions FROM ledger_runtime;");
            migrationBuilder.Sql("GRANT UPDATE, DELETE ON TABLE transactions, accounts, bank_connections, sync_runs TO ledger_runtime;");
            migrationBuilder.Sql("GRANT UPDATE, DELETE ON TABLE transaction_payloads, transaction_refs TO ledger_runtime;");

            migrationBuilder.DropTable(
                name: "transaction_payloads");

            migrationBuilder.DropTable(
                name: "transaction_refs");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "bank_connections");
        }
    }
}
