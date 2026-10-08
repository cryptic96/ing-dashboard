using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Repository.Migrations
{
    /// <inheritdoc />
    public partial class TrackLastTotpTimeStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_totp_accepted_at",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "last_totp_code_sha256",
                table: "identity_users");

            migrationBuilder.AddColumn<long>(
                name: "last_totp_step",
                table: "identity_users",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_totp_step",
                table: "identity_users");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_totp_accepted_at",
                table: "identity_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "last_totp_code_sha256",
                table: "identity_users",
                type: "bytea",
                nullable: true);
        }
    }
}
