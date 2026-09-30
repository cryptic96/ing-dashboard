using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddBalanceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "balance_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_date = table.Column<DateOnly>(type: "date", nullable: false),
                    balance_kind = table.Column<string>(type: "text", nullable: false),
                    provider_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    reference_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expected_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    drift_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    reconciled = table.Column<bool>(type: "boolean", nullable: true),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_balance_snapshots", x => x.id);
                    table.CheckConstraint("ck_balance_snapshots_balance_kind", "balance_kind IN ('closing_booked', 'interim_booked', 'interim_available', 'closing_available', 'opening_booked', 'expected', 'other')");
                    table.CheckConstraint("ck_balance_snapshots_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_balance_snapshots_verdict", "reconciled IS NULL OR (expected_amount IS NOT NULL AND drift_amount IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_balance_snapshots_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_balance_snapshots_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_balance_snapshots_sync_run_id",
                table: "balance_snapshots",
                column: "sync_run_id");

            migrationBuilder.CreateIndex(
                name: "ux_balance_snapshots_account_date_kind",
                table: "balance_snapshots",
                columns: new[] { "account_id", "snapshot_date", "balance_kind" },
                unique: true);

            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE, TRUNCATE ON TABLE balance_snapshots FROM ledger_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "balance_snapshots");
        }
    }
}
