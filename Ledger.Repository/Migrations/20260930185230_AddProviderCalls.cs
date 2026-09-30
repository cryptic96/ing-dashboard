using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    called_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    background = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_calls", x => x.id);
                    table.CheckConstraint("ck_provider_calls_kind", "kind IN ('transactions', 'balances')");
                    table.ForeignKey(
                        name: "fk_provider_calls_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_provider_calls_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_calls_account_id_called_at",
                table: "provider_calls",
                columns: new[] { "account_id", "called_at" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_calls_sync_run_id",
                table: "provider_calls",
                column: "sync_run_id");

            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE, TRUNCATE ON TABLE provider_calls FROM ledger_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_calls");
        }
    }
}
