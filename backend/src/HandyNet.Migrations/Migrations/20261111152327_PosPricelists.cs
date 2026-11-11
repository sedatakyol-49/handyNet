using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosPricelists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "pricelist_id",
                schema: "pos",
                table: "receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pricelist_id",
                schema: "pos",
                table: "customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pricelists",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    discount_basis_points = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pricelists", x => x.id);
                    table.UniqueConstraint("ak_pricelist_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_pricelists_discount", "discount_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_pricelists_validity", "valid_from IS NULL OR valid_to IS NULL OR valid_from <= valid_to");
                });

            migrationBuilder.CreateTable(
                name: "pricelist_items",
                schema: "pos",
                columns: table => new
                {
                    pricelist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pricelist_items", x => new { x.pricelist_id, x.product_id });
                    table.CheckConstraint("ck_pricelist_items_price", "price_minor > 0");
                    table.ForeignKey(
                        name: "fk_pricelist_items_pricelists_organization_id_pricelist_id",
                        columns: x => new { x.organization_id, x.pricelist_id },
                        principalSchema: "pos",
                        principalTable: "pricelists",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pricelist_items_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_pricelist_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "pricelist_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_organization_id_pricelist_id",
                schema: "pos",
                table: "customers",
                columns: new[] { "organization_id", "pricelist_id" },
                filter: "pricelist_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pricelist_items_organization_id_pricelist_id",
                schema: "pos",
                table: "pricelist_items",
                columns: new[] { "organization_id", "pricelist_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pricelist_items_organization_id_product_id",
                schema: "pos",
                table: "pricelist_items",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pricelist_items_product_id",
                schema: "pos",
                table: "pricelist_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_pricelists_organization_id_name",
                schema: "pos",
                table: "pricelists",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_customers_pricelist_organization_id_pricelist_id",
                schema: "pos",
                table: "customers",
                columns: new[] { "organization_id", "pricelist_id" },
                principalSchema: "pos",
                principalTable: "pricelists",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_receipts_pricelist_organization_id_pricelist_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "pricelist_id" },
                principalSchema: "pos",
                principalTable: "pricelists",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            PosPricelistsSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosPricelistsSql.Down(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_customers_pricelist_organization_id_pricelist_id",
                schema: "pos",
                table: "customers");

            migrationBuilder.DropForeignKey(
                name: "fk_receipts_pricelist_organization_id_pricelist_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropTable(
                name: "pricelist_items",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "pricelists",
                schema: "pos");

            migrationBuilder.DropIndex(
                name: "ix_receipts_organization_id_pricelist_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropIndex(
                name: "ix_customers_organization_id_pricelist_id",
                schema: "pos",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "pricelist_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "pricelist_id",
                schema: "pos",
                table: "customers");
        }
    }
}
