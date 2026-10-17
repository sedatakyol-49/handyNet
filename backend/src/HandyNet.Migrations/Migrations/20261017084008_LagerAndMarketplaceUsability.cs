using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class LagerAndMarketplaceUsability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "unit_cost_minor",
                schema: "inventory",
                table: "stock_movements",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "model_keys",
                schema: "catalog",
                table: "products",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            // Backfill: same normalization as Text.NormalizeKey (upper case, no whitespace, '-', '_', '.').
            migrationBuilder.Sql("""
                UPDATE catalog.products p
                   SET model_keys = COALESCE((
                       SELECT array_agg(DISTINCT s.k)
                         FROM (SELECT upper(regexp_replace(m, '[[:space:]._-]', '', 'g')) AS k
                                 FROM unnest(array_prepend(p.model_name, p.compatible_models)) AS m
                                WHERE m IS NOT NULL) s
                        WHERE s.k <> ''), '{}'::text[]);
                ALTER TABLE catalog.products ALTER COLUMN model_keys DROP DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "manual_entries",
                schema: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_received = table.Column<int>(type: "integer", nullable: false),
                    unit_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    unit_cost_minor = table.Column<long>(type: "bigint", nullable: true),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_entries", x => x.id);
                    table.CheckConstraint("ck_manual_entries_offer", "(offer_id IS NULL) = (offer_action = 'None')");
                    table.CheckConstraint("ck_manual_entries_quantity", "quantity_received >= 0");
                    table.CheckConstraint("ck_manual_entries_unit_cost", "unit_cost_minor IS NULL OR unit_cost_minor >= 0");
                    table.CheckConstraint("ck_manual_entries_units", "cardinality(unit_ids) = 0 OR cardinality(unit_ids) = quantity_received");
                    table.ForeignKey(
                        name: "fk_manual_entries_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manual_entries_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_images",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: true),
                    storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    thumbnail_storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    thumbnail_width = table.Column<int>(type: "integer", nullable: false),
                    thumbnail_height = table.Column<int>(type: "integer", nullable: false),
                    thumbnail_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    original_format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    original_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_images", x => x.id);
                    table.CheckConstraint("ck_product_images_content_type", "content_type IN ('image/webp', 'image/jpeg', 'image/png')");
                    table.CheckConstraint("ck_product_images_position", "position IS NULL OR (position >= 0 AND position < 8)");
                    table.CheckConstraint("ck_product_images_removed", "(removed_at IS NULL) = (position IS NOT NULL)");
                    table.CheckConstraint("ck_product_images_sizes", "size_bytes > 0 AND thumbnail_size_bytes > 0 AND original_size_bytes > 0 AND width > 0 AND height > 0 AND thumbnail_width > 0 AND thumbnail_height > 0");
                    table.ForeignKey(
                        name: "fk_product_images_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_unit_cost",
                schema: "inventory",
                table: "stock_movements",
                sql: "unit_cost_minor IS NULL OR unit_cost_minor >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_products_brand",
                schema: "catalog",
                table: "products",
                column: "brand");

            migrationBuilder.CreateIndex(
                name: "ix_products_model_keys",
                schema: "catalog",
                table: "products",
                column: "model_keys")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_products_quality",
                schema: "catalog",
                table: "products",
                column: "quality");

            migrationBuilder.CreateIndex(
                name: "ix_offers_status_delivery_days",
                schema: "marketplace",
                table: "offers",
                columns: new[] { "status", "delivery_days" });

            migrationBuilder.CreateIndex(
                name: "ix_offers_status_price_minor",
                schema: "marketplace",
                table: "offers",
                columns: new[] { "status", "price_minor" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_entries_organization_id_created_at",
                schema: "imports",
                table: "manual_entries",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_entries_organization_id_idempotency_key",
                schema: "imports",
                table: "manual_entries",
                columns: new[] { "organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_manual_entries_organization_id_warehouse_id",
                schema: "imports",
                table: "manual_entries",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_entries_product_id",
                schema: "imports",
                table: "manual_entries",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_active",
                schema: "catalog",
                table: "product_images",
                column: "product_id",
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_storage_key",
                schema: "catalog",
                table: "product_images",
                column: "storage_key",
                unique: true);

            migrationBuilder.Sql(SecurityAndViewUp);
        }

        /// <summary>
        /// - manual_entries: tenant-owned, RLS per organization, insert-only for the app role (no UPDATE, no DELETE).
        /// - product_images: canonical catalog data readable by everyone; writes only for the organization that created
        ///   the product, or with no tenant (platform administration, authorized in the application). No DELETE grant
        ///   (removal = removed_at). Unique (product, position) is DEFERRABLE so a reorder may swap positions.
        /// - offer_listings view: adds model name, compatible models, model keys, image count and primary image.
        /// </summary>
        private const string SecurityAndViewUp = """
            ALTER TABLE imports.manual_entries ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON imports.manual_entries
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT ON imports.manual_entries TO handynet_app;
            REVOKE UPDATE, DELETE ON imports.manual_entries FROM handynet_app;

            ALTER TABLE catalog.product_images
                ADD CONSTRAINT ux_product_images_position UNIQUE (product_id, position) DEFERRABLE INITIALLY DEFERRED;
            ALTER TABLE catalog.product_images ENABLE ROW LEVEL SECURITY;
            CREATE POLICY read_all ON catalog.product_images FOR SELECT USING (true);
            CREATE POLICY product_owner_inserts ON catalog.product_images FOR INSERT
                WITH CHECK (uploaded_by_organization_id IS NOT DISTINCT FROM infra.current_org()
                            AND (infra.current_org() IS NULL
                                 OR EXISTS (SELECT 1 FROM catalog.products p
                                            WHERE p.id = product_id AND p.created_by_organization_id = infra.current_org())));
            CREATE POLICY product_owner_updates ON catalog.product_images FOR UPDATE
                USING (infra.current_org() IS NULL
                       OR EXISTS (SELECT 1 FROM catalog.products p
                                  WHERE p.id = product_id AND p.created_by_organization_id = infra.current_org()))
                WITH CHECK (infra.current_org() IS NULL
                            OR EXISTS (SELECT 1 FROM catalog.products p
                                       WHERE p.id = product_id AND p.created_by_organization_id = infra.current_org()));
            GRANT SELECT, INSERT, UPDATE ON catalog.product_images TO handynet_app;
            REVOKE DELETE ON catalog.product_images FROM handynet_app;

            CREATE OR REPLACE VIEW marketplace.offer_listings WITH (security_barrier = true) AS
            SELECT o.id                      AS offer_id,
                   o.organization_id         AS seller_organization_id,
                   org.name                  AS seller_name,
                   org.city                  AS seller_city,
                   p.id                      AS product_id,
                   p.brand,
                   p.name                    AS product_name,
                   p.category,
                   p.quality,
                   p.ean,
                   p.mpn,
                   o.supplier_sku,
                   o.price_minor,
                   o.currency,
                   o.min_order_quantity,
                   o.delivery_days,
                   o.condition,
                   GREATEST(COALESCE(s.on_hand - s.reserved, 0), 0) AS available,
                   o.updated_at,
                   p.model_name,
                   p.compatible_models,
                   p.model_keys,
                   img.image_count,
                   img.primary_image_id
            FROM marketplace.offers o
            JOIN catalog.products p ON p.id = o.product_id
            JOIN tenancy.organizations org ON org.id = o.organization_id AND org.status = 'Verified'
            LEFT JOIN inventory.stock_levels s ON s.warehouse_id = o.warehouse_id AND s.product_id = o.product_id
            CROSS JOIN LATERAL (SELECT count(*)::integer AS image_count,
                                       (array_agg(i.id ORDER BY i.position))[1] AS primary_image_id
                                  FROM catalog.product_images i
                                 WHERE i.product_id = p.id AND i.removed_at IS NULL) img
            WHERE o.status = 'Published'
              AND infra.current_org() IS NOT NULL
              AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                          WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales');
            """;

        /// <summary>The view exactly as defined in TenantIsolationAndReservations (columns can only be dropped by re-creating it).</summary>
        private const string ViewDown = """
            DROP VIEW IF EXISTS marketplace.offer_listings;
            CREATE VIEW marketplace.offer_listings WITH (security_barrier = true) AS
            SELECT o.id                      AS offer_id,
                   o.organization_id         AS seller_organization_id,
                   org.name                  AS seller_name,
                   org.city                  AS seller_city,
                   p.id                      AS product_id,
                   p.brand,
                   p.name                    AS product_name,
                   p.category,
                   p.quality,
                   p.ean,
                   p.mpn,
                   o.supplier_sku,
                   o.price_minor,
                   o.currency,
                   o.min_order_quantity,
                   o.delivery_days,
                   o.condition,
                   GREATEST(COALESCE(s.on_hand - s.reserved, 0), 0) AS available,
                   o.updated_at
            FROM marketplace.offers o
            JOIN catalog.products p ON p.id = o.product_id
            JOIN tenancy.organizations org ON org.id = o.organization_id AND org.status = 'Verified'
            LEFT JOIN inventory.stock_levels s ON s.warehouse_id = o.warehouse_id AND s.product_id = o.product_id
            WHERE o.status = 'Published'
              AND infra.current_org() IS NOT NULL
              AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                          WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales');
            GRANT SELECT ON marketplace.offer_listings TO handynet_app;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ViewDown);

            migrationBuilder.DropTable(
                name: "manual_entries",
                schema: "imports");

            migrationBuilder.DropTable(
                name: "product_images",
                schema: "catalog");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_unit_cost",
                schema: "inventory",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "ix_products_brand",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_model_keys",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_quality",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_offers_status_delivery_days",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropIndex(
                name: "ix_offers_status_price_minor",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "unit_cost_minor",
                schema: "inventory",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "model_keys",
                schema: "catalog",
                table: "products");
        }
    }
}
