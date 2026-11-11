using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosOfflineSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "offline_mode",
                schema: "pos",
                table: "registers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offline_recorded_at",
                schema: "pos",
                table: "receipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offline_reference",
                schema: "pos",
                table: "receipts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_receipts_offline_reference",
                schema: "pos",
                table: "receipts",
                columns: new[] { "register_id", "offline_reference" },
                unique: true,
                filter: "offline_reference IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipts_offline",
                schema: "pos",
                table: "receipts",
                sql: "(offline_recorded_at IS NULL AND offline_reference IS NULL) OR (offline_recorded_at IS NOT NULL AND offline_reference ~ '^[A-Za-z0-9-]{1,40}$' AND kind = 'Sale')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_receipts_offline_reference",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipts_offline",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "offline_mode",
                schema: "pos",
                table: "registers");

            migrationBuilder.DropColumn(
                name: "offline_recorded_at",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "offline_reference",
                schema: "pos",
                table: "receipts");
        }
    }
}
