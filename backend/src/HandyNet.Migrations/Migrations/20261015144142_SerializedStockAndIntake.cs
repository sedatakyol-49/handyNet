using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SerializedStockAndIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "intakes",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imei = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: false),
                    seller = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by = table.Column<Guid>(type: "uuid", nullable: false),
                    inspected_condition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    inspected_defects = table.Column<string[]>(type: "text[]", nullable: false),
                    inspected_battery_health_percent = table.Column<int>(type: "integer", nullable: true),
                    inspection_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    inspected_offer_minor = table.Column<long>(type: "bigint", nullable: true),
                    inspected_max_consumer_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    inspected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_minor = table.Column<long>(type: "bigint", nullable: true),
                    payment_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    stock_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intakes", x => x.id);
                    table.CheckConstraint("ck_intakes_paid", "paid_minor IS NULL OR paid_minor > 0");
                    table.ForeignKey(
                        name: "fk_intakes_quote_quote_id",
                        column: x => x.quote_id,
                        principalSchema: "ankauf",
                        principalTable: "quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_units",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imei = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tax_scheme = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    purchase_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_units", x => x.id);
                    table.CheckConstraint("ck_stock_units_purchase_price", "purchase_price_minor >= 0");
                    table.ForeignKey(
                        name: "fk_stock_units_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_units_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intakes_imei",
                schema: "ankauf",
                table: "intakes",
                column: "imei");

            migrationBuilder.CreateIndex(
                name: "ix_intakes_organization_id_received_at",
                schema: "ankauf",
                table: "intakes",
                columns: new[] { "organization_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_intakes_one_per_quote",
                schema: "ankauf",
                table: "intakes",
                column: "quote_id",
                unique: true,
                filter: "status <> 'Rejected'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_units_organization_id_product_id_status",
                schema: "inventory",
                table: "stock_units",
                columns: new[] { "organization_id", "product_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_units_organization_id_warehouse_id",
                schema: "inventory",
                table: "stock_units",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_units_product_id",
                schema: "inventory",
                table: "stock_units",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_units_imei_in_stock",
                schema: "inventory",
                table: "stock_units",
                column: "imei",
                unique: true,
                filter: "status IN ('InStock', 'Reserved')");

            // Tenant-owned: stock units and intakes (intakes hold the seller's personal data). No DELETE for the app.
            // inventory.imei_in_stock answers only yes/no across tenants, so no other organization's stock is exposed.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON inventory.stock_units TO handynet_app;
                GRANT SELECT, INSERT, UPDATE ON ankauf.intakes TO handynet_app;

                ALTER TABLE inventory.stock_units ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON inventory.stock_units
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE ankauf.intakes ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON ankauf.intakes
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                CREATE FUNCTION inventory.imei_in_stock(p_imei text) RETURNS boolean
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$ SELECT EXISTS (SELECT 1 FROM inventory.stock_units u WHERE u.imei = p_imei AND u.status IN ('InStock', 'Reserved')) $$;
                REVOKE ALL ON FUNCTION inventory.imei_in_stock(text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION inventory.imei_in_stock(text) TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS inventory.imei_in_stock(text);");
            migrationBuilder.DropTable(
                name: "intakes",
                schema: "ankauf");

            migrationBuilder.DropTable(
                name: "stock_units",
                schema: "inventory");
        }
    }
}
