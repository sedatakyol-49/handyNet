using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosOdooParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "customer_id",
                schema: "pos",
                table: "receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "pos",
                table: "receipt_lines",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pos_max_discount_percent",
                schema: "tenancy",
                table: "memberships",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "pos_permissions",
                schema: "tenancy",
                table: "memberships",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closing_denominations",
                schema: "pos",
                table: "cash_sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "opening_denominations",
                schema: "pos",
                table: "cash_sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                schema: "pos",
                table: "cash_movements",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    vat_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.UniqueConstraint("ak_customer_organization_id_id", x => new { x.organization_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "prices",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prices", x => new { x.organization_id, x.product_id });
                    table.CheckConstraint("ck_prices_price", "price_minor IS NULL OR price_minor > 0");
                    table.ForeignKey(
                        name: "fk_prices_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_reprints",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    printed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    printed_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_reprints", x => x.id);
                    table.CheckConstraint("ck_receipt_reprints_sequence", "sequence >= 1");
                    table.ForeignKey(
                        name: "fk_receipt_reprints_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_created_by_created_at",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "created_by", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_customer_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "customer_id" },
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_lines_organization_id_product_id",
                schema: "pos",
                table: "receipt_lines",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_memberships_pos_max_discount",
                schema: "tenancy",
                table: "memberships",
                sql: "pos_max_discount_percent BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_memberships_pos_rights",
                schema: "tenancy",
                table: "memberships",
                sql: "role = 'Staff' OR (pos_permissions IS NULL AND pos_max_discount_percent IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_customers_organization_id_name",
                schema: "pos",
                table: "customers",
                columns: new[] { "organization_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_prices_product_id",
                schema: "pos",
                table: "prices",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reprints_organization_id_receipt_id",
                schema: "pos",
                table: "receipt_reprints",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reprints_receipt_id_sequence",
                schema: "pos",
                table: "receipt_reprints",
                columns: new[] { "receipt_id", "sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_receipts_customer_organization_id_customer_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "customer_id" },
                principalSchema: "pos",
                principalTable: "customers",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ---- Grants: customers and POS prices are master data (no DELETE); reprints are append-only ---------------------
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON pos.customers, pos.prices TO handynet_app;
                REVOKE DELETE, TRUNCATE ON pos.customers, pos.prices FROM handynet_app;
                GRANT SELECT, INSERT ON pos.receipt_reprints TO handynet_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON pos.receipt_reprints FROM handynet_app;
                """);

            // ---- Row-Level Security --------------------------------------------------------------------------------------------
            foreach (var table in new[] { "customers", "prices", "receipt_reprints" })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE pos.{table} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON pos.{table}
                        USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                    """);
            }

            // ---- Immutability: reprints never change (even for the owner); the opening count of a session is fixed ----------
            migrationBuilder.Sql("""
                CREATE TRIGGER receipt_reprints_immutable BEFORE UPDATE OR DELETE ON pos.receipt_reprints
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER receipt_reprints_no_truncate BEFORE TRUNCATE ON pos.receipt_reprints
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

                CREATE FUNCTION pos.fix_session_opening() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.opening_cash_minor <> OLD.opening_cash_minor
                       OR NEW.opening_denominations IS DISTINCT FROM OLD.opening_denominations
                       OR NEW.opened_at <> OLD.opened_at OR NEW.opened_by <> OLD.opened_by THEN
                        RAISE EXCEPTION 'the opening count of a cash session cannot be changed' USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER cash_sessions_opening_fixed BEFORE UPDATE ON pos.cash_sessions
                    FOR EACH ROW EXECUTE FUNCTION pos.fix_session_opening();
                """);

            // ---- Read model for the till's product grid (ADR-0001: cross-module reads as SQL views) ---------------------------
            // Direction POS → Inventory/Catalog only (allowed by the dependency map). security_invoker: the caller's RLS policies
            // on stock levels, stock units and POS prices apply, so a tenant sees only its own stock and prices.
            migrationBuilder.Sql("""
                CREATE VIEW pos.sellable_stock WITH (security_invoker = true) AS
                SELECT s.organization_id,
                       s.warehouse_id,
                       s.product_id,
                       p.brand,
                       p.name,
                       p.category,
                       p.quality,
                       p.ean,
                       p.mpn,
                       p.model_name,
                       s.on_hand,
                       s.reserved,
                       s.on_hand - s.reserved AS available,
                       EXISTS (SELECT 1 FROM inventory.stock_units u
                               WHERE u.organization_id = s.organization_id AND u.warehouse_id = s.warehouse_id
                                 AND u.product_id = s.product_id AND u.status IN ('InStock', 'Reserved')) AS serialized,
                       pp.price_minor AS pos_price_minor
                FROM inventory.stock_levels s
                JOIN catalog.products p ON p.id = s.product_id
                LEFT JOIN pos.prices pp ON pp.organization_id = s.organization_id AND pp.product_id = s.product_id;
                GRANT SELECT ON pos.sellable_stock TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS pos.sellable_stock;
                DROP TRIGGER IF EXISTS cash_sessions_opening_fixed ON pos.cash_sessions;
                DROP FUNCTION IF EXISTS pos.fix_session_opening();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_receipts_customer_organization_id_customer_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "prices",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "receipt_reprints",
                schema: "pos");

            migrationBuilder.DropIndex(
                name: "ix_receipts_organization_id_created_by_created_at",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropIndex(
                name: "ix_receipts_organization_id_customer_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropIndex(
                name: "ix_receipt_lines_organization_id_product_id",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_memberships_pos_max_discount",
                schema: "tenancy",
                table: "memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_memberships_pos_rights",
                schema: "tenancy",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "customer_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropColumn(
                name: "pos_max_discount_percent",
                schema: "tenancy",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "pos_permissions",
                schema: "tenancy",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "closing_denominations",
                schema: "pos",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "opening_denominations",
                schema: "pos",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "reason_code",
                schema: "pos",
                table: "cash_movements");
        }
    }
}
