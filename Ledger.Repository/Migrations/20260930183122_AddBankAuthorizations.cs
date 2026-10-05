using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddBankAuthorizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_authorization_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_authorizations", x => x.id);
                    table.CheckConstraint("ck_bank_authorizations_purpose", "purpose IN ('link', 'renew')");
                    table.ForeignKey(
                        name: "fk_bank_authorizations_bank_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_authorizations_connection_id",
                table: "bank_authorizations",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_authorizations_state_sha256",
                table: "bank_authorizations",
                column: "state_sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_authorizations");
        }
    }
}
