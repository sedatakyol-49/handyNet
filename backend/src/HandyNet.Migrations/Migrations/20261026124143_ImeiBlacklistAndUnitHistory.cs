using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ImeiBlacklistAndUnitHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "unit_id",
                schema: "inventory",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "imei_blacklist",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    imei = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deactivation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imei_blacklist", x => x.id);
                    table.CheckConstraint("ck_imei_blacklist_deactivation", "(deactivated_at IS NULL AND deactivated_by_user_id IS NULL AND deactivation_reason IS NULL) OR (deactivated_at IS NOT NULL AND deactivated_by_user_id IS NOT NULL AND deactivation_reason IS NOT NULL AND btrim(deactivation_reason) <> '')");
                    table.CheckConstraint("ck_imei_blacklist_imei", "imei ~ '^[0-9]{15}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_unit_id",
                schema: "inventory",
                table: "stock_movements",
                column: "unit_id",
                filter: "unit_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_imei_blacklist_imei_created_at",
                schema: "inventory",
                table: "imei_blacklist",
                columns: new[] { "imei", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_imei_blacklist_active",
                schema: "inventory",
                table: "imei_blacklist",
                column: "imei",
                unique: true,
                filter: "deactivated_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movements_stock_unit_unit_id",
                schema: "inventory",
                table: "stock_movements",
                column: "unit_id",
                principalSchema: "inventory",
                principalTable: "stock_units",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            ImeiBlacklistAndUnitHistorySql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ImeiBlacklistAndUnitHistorySql.Down(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movements_stock_unit_unit_id",
                schema: "inventory",
                table: "stock_movements");

            migrationBuilder.DropTable(
                name: "imei_blacklist",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_unit_id",
                schema: "inventory",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "unit_id",
                schema: "inventory",
                table: "stock_movements");
        }
    }
}
