using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_canary",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    protected_payload = table.Column<string>(type: "text", nullable: false),
                    plaintext_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_canary", x => x.id);
                    table.CheckConstraint("ck_data_protection_canary_id", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.Sql("CREATE SCHEMA reporting;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA reporting TO grafana_reader;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA reporting GRANT SELECT ON TABLES TO grafana_reader;");
            migrationBuilder.Sql(
                "REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE \"__EFMigrationsHistory\" FROM ledger_runtime;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "GRANT INSERT, UPDATE, DELETE, TRUNCATE ON TABLE \"__EFMigrationsHistory\" TO ledger_runtime;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA reporting REVOKE SELECT ON TABLES FROM grafana_reader;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA reporting FROM grafana_reader;");
            migrationBuilder.Sql("DROP SCHEMA reporting;");

            migrationBuilder.DropTable(
                name: "data_protection_canary");

            migrationBuilder.DropTable(
                name: "data_protection_keys");
        }
    }
}
