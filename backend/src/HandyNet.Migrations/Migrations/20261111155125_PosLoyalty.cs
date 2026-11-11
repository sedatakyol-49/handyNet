using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosLoyalty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_receipt_lines_amounts",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.AddColumn<int>(
                name: "loyalty_balance_after",
                schema: "pos",
                table: "receipts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "loyalty_discount_minor",
                schema: "pos",
                table: "receipts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "loyalty_points_earned",
                schema: "pos",
                table: "receipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "loyalty_points_redeemed",
                schema: "pos",
                table: "receipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "loyalty_discount_minor",
                schema: "pos",
                table: "receipt_lines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "loyalty_accounts",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_accounts", x => new { x.organization_id, x.customer_id });
                    table.CheckConstraint("ck_loyalty_accounts_points", "points >= 0");
                    table.ForeignKey(
                        name: "fk_loyalty_accounts_customers_organization_id_customer_id",
                        columns: x => new { x.organization_id, x.customer_id },
                        principalSchema: "pos",
                        principalTable: "customers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_settings",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    points_per_euro = table.Column<int>(type: "integer", nullable: false),
                    point_value_minor = table.Column<int>(type: "integer", nullable: false),
                    min_redeem_points = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_settings", x => x.organization_id);
                    table.CheckConstraint("ck_loyalty_settings_values", "points_per_euro BETWEEN 0 AND 100 AND point_value_minor BETWEEN 1 AND 100 AND min_redeem_points BETWEEN 0 AND 1000000");
                });

            migrationBuilder.CreateTable(
                name: "loyalty_transactions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    balance_after = table.Column<int>(type: "integer", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_transactions", x => x.id);
                    table.CheckConstraint("ck_loyalty_transactions_points", "balance_after >= 0 AND points <> 0 AND ((type IN ('Earn', 'ReverseRedeem') AND points > 0) OR (type IN ('Redeem', 'ReverseEarn') AND points < 0) OR (type = 'Adjust' AND reason IS NOT NULL))");
                    table.CheckConstraint("ck_loyalty_transactions_type", "type IN ('Earn', 'Redeem', 'ReverseEarn', 'ReverseRedeem', 'Adjust')");
                    table.ForeignKey(
                        name: "fk_loyalty_transactions_loyalty_accounts_organization_id_custo",
                        columns: x => new { x.organization_id, x.customer_id },
                        principalSchema: "pos",
                        principalTable: "loyalty_accounts",
                        principalColumns: new[] { "organization_id", "customer_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loyalty_transactions_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipt_lines_amounts",
                schema: "pos",
                table: "receipt_lines",
                sql: "line_gross_minor = unit_price_minor * quantity - discount_minor - loyalty_discount_minor AND net_minor + vat_minor = line_gross_minor");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipt_lines_loyalty",
                schema: "pos",
                table: "receipt_lines",
                sql: "loyalty_discount_minor = 0 OR (sign(loyalty_discount_minor) = sign(quantity) AND gift_card_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_transactions_organization_id_customer_id_created_at",
                schema: "pos",
                table: "loyalty_transactions",
                columns: new[] { "organization_id", "customer_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_transactions_organization_id_receipt_id",
                schema: "pos",
                table: "loyalty_transactions",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_transactions_receipt_id",
                schema: "pos",
                table: "loyalty_transactions",
                column: "receipt_id",
                filter: "receipt_id IS NOT NULL");

            PosLoyaltySql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosLoyaltySql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "loyalty_settings",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "loyalty_transactions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "loyalty_accounts",
                schema: "pos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipt_lines_amounts",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipt_lines_loyalty",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropColumn(
                name: "loyalty_balance_after",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "loyalty_discount_minor",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "loyalty_points_earned",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "loyalty_points_redeemed",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "loyalty_discount_minor",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipt_lines_amounts",
                schema: "pos",
                table: "receipt_lines",
                sql: "line_gross_minor = unit_price_minor * quantity - discount_minor AND net_minor + vat_minor = line_gross_minor");
        }
    }
}
