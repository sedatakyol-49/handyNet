using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <summary>
    /// Database-level tenant isolation (ADR-0003):
    /// - the API connects as <c>handynet_app</c>: no DDL, no DELETE on business data, no UPDATE on
    ///   append-only tables (audit log, stock movements);
    /// - Row-Level Security on every tenant-owned table, keyed on the session setting app.current_org;
    /// - cross-tenant operations go only through narrow SECURITY DEFINER objects defined here.
    /// The role is created NOLOGIN; operators grant LOGIN + password per environment (see docs/operations.md).
    /// </summary>
    public partial class TenantIsolationAndReservations : Migration
    {
        private static readonly string[] Schemas = ["identity", "tenancy", "catalog", "inventory", "marketplace", "imports", "ordering", "infra"];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'handynet_app') THEN
                        CREATE ROLE handynet_app NOLOGIN;
                    END IF;
                END $$;
                """);

            foreach (var schema in Schemas)
            {
                migrationBuilder.Sql($"""
                    GRANT USAGE ON SCHEMA {schema} TO handynet_app;
                    GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA {schema} TO handynet_app;
                    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO handynet_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE ON TABLES TO handynet_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT USAGE, SELECT ON SEQUENCES TO handynet_app;
                    """);
            }

            migrationBuilder.Sql("""
                -- Migration bookkeeping is not the application's business.
                REVOKE ALL ON infra.__ef_migrations_history FROM handynet_app;

                -- Append-only history: rows can be added and read, never changed.
                REVOKE UPDATE ON infra.audit_log FROM handynet_app;
                REVOKE UPDATE ON inventory.stock_movements FROM handynet_app;

                -- The only DELETE the application needs: disabling a module for an organization.
                GRANT DELETE ON tenancy.organization_modules TO handynet_app;

                CREATE FUNCTION infra.current_org() RETURNS uuid
                    LANGUAGE sql STABLE
                    AS $$ SELECT nullif(current_setting('app.current_org', true), '')::uuid $$;
                GRANT EXECUTE ON FUNCTION infra.current_org() TO handynet_app;
                """);

            // ---- Row-Level Security --------------------------------------------------------------
            migrationBuilder.Sql("""
                ALTER TABLE inventory.warehouses ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON inventory.warehouses
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE inventory.stock_levels ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON inventory.stock_levels
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE inventory.stock_movements ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON inventory.stock_movements
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE inventory.stock_reservations ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON inventory.stock_reservations
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                CREATE POLICY reserving_party_reads ON inventory.stock_reservations FOR SELECT
                    USING (reserved_by_organization_id = infra.current_org());

                ALTER TABLE marketplace.offers ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON marketplace.offers
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE imports.import_batches ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON imports.import_batches
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE imports.import_rows ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON imports.import_rows
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

                ALTER TABLE ordering.orders ENABLE ROW LEVEL SECURITY;
                CREATE POLICY order_parties ON ordering.orders
                    USING (buyer_organization_id = infra.current_org() OR seller_organization_id = infra.current_org())
                    WITH CHECK (buyer_organization_id = infra.current_org() OR seller_organization_id = infra.current_org());

                ALTER TABLE ordering.order_lines ENABLE ROW LEVEL SECURITY;
                -- The sub-select is itself filtered by the orders policy.
                CREATE POLICY order_parties ON ordering.order_lines
                    USING (EXISTS (SELECT 1 FROM ordering.orders o WHERE o.id = order_id))
                    WITH CHECK (EXISTS (SELECT 1 FROM ordering.orders o WHERE o.id = order_id));
                """);

            // ---- B2B listing view ------------------------------------------------------------------
            // Owned by the migration role, so it reads across tenants; it exposes only published offers
            // of verified sellers with B2B sales enabled, and only to a request that has a tenant.
            // Anonymous / public storefront requests (no tenant) see nothing: B2B offers never leak to B2C.
            migrationBuilder.Sql("""
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
                """);

            // ---- Atomic cross-tenant reservation ---------------------------------------------------
            // One conditional UPDATE decides availability under a row lock, so two concurrent checkouts
            // for the last unit cannot both succeed (READ COMMITTED re-checks the WHERE after the lock).
            migrationBuilder.Sql("""
                CREATE FUNCTION marketplace.reserve_offer_stock(p_offer_id uuid, p_quantity integer, p_reference text)
                    RETURNS uuid
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_buyer uuid := infra.current_org();
                    v_offer record;
                    v_reservation uuid;
                BEGIN
                    IF v_buyer IS NULL OR p_quantity IS NULL OR p_quantity <= 0 THEN
                        RETURN NULL;
                    END IF;

                    -- The buyer must be a verified organization with B2B purchasing enabled.
                    IF NOT EXISTS (SELECT 1 FROM tenancy.organizations b
                                   JOIN tenancy.organization_modules m ON m.organization_id = b.id AND m.module = 'B2BPurchasing'
                                   WHERE b.id = v_buyer AND b.status = 'Verified') THEN
                        RETURN NULL;
                    END IF;

                    SELECT o.organization_id, o.warehouse_id, o.product_id, o.min_order_quantity
                      INTO v_offer
                      FROM marketplace.offers o
                      JOIN tenancy.organizations s ON s.id = o.organization_id AND s.status = 'Verified'
                     WHERE o.id = p_offer_id
                       AND o.status = 'Published'
                       AND o.organization_id <> v_buyer
                       AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                                   WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales');

                    IF NOT FOUND OR p_quantity < v_offer.min_order_quantity THEN
                        RETURN NULL;
                    END IF;

                    UPDATE inventory.stock_levels
                       SET reserved = reserved + p_quantity, updated_at = now()
                     WHERE warehouse_id = v_offer.warehouse_id
                       AND product_id = v_offer.product_id
                       AND on_hand - reserved >= p_quantity;

                    IF NOT FOUND THEN
                        RETURN NULL;
                    END IF;

                    INSERT INTO inventory.stock_reservations
                        (id, organization_id, warehouse_id, product_id, quantity, status, reference, reserved_by_organization_id, created_at)
                    VALUES
                        (gen_random_uuid(), v_offer.organization_id, v_offer.warehouse_id, v_offer.product_id, p_quantity,
                         'Active', p_reference, v_buyer, now())
                    RETURNING id INTO v_reservation;

                    RETURN v_reservation;
                END
                $$;

                CREATE FUNCTION inventory.release_reservation(p_reservation_id uuid)
                    RETURNS boolean
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_org uuid := infra.current_org();
                    v_res record;
                BEGIN
                    IF v_org IS NULL THEN
                        RETURN false;
                    END IF;

                    UPDATE inventory.stock_reservations
                       SET status = 'Released', closed_at = now()
                     WHERE id = p_reservation_id
                       AND status = 'Active'
                       AND (organization_id = v_org OR reserved_by_organization_id = v_org)
                    RETURNING warehouse_id, product_id, quantity INTO v_res;

                    IF NOT FOUND THEN
                        RETURN false;
                    END IF;

                    UPDATE inventory.stock_levels
                       SET reserved = reserved - v_res.quantity, updated_at = now()
                     WHERE warehouse_id = v_res.warehouse_id AND product_id = v_res.product_id;

                    RETURN true;
                END
                $$;

                REVOKE ALL ON FUNCTION marketplace.reserve_offer_stock(uuid, integer, text) FROM PUBLIC;
                REVOKE ALL ON FUNCTION inventory.release_reservation(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION marketplace.reserve_offer_stock(uuid, integer, text) TO handynet_app;
                GRANT EXECUTE ON FUNCTION inventory.release_reservation(uuid) TO handynet_app;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS inventory.release_reservation(uuid);
                DROP FUNCTION IF EXISTS marketplace.reserve_offer_stock(uuid, integer, text);
                DROP VIEW IF EXISTS marketplace.offer_listings;

                DROP POLICY IF EXISTS order_parties ON ordering.order_lines;
                DROP POLICY IF EXISTS order_parties ON ordering.orders;
                DROP POLICY IF EXISTS tenant_isolation ON imports.import_rows;
                DROP POLICY IF EXISTS tenant_isolation ON imports.import_batches;
                DROP POLICY IF EXISTS tenant_isolation ON marketplace.offers;
                DROP POLICY IF EXISTS reserving_party_reads ON inventory.stock_reservations;
                DROP POLICY IF EXISTS tenant_isolation ON inventory.stock_reservations;
                DROP POLICY IF EXISTS tenant_isolation ON inventory.stock_movements;
                DROP POLICY IF EXISTS tenant_isolation ON inventory.stock_levels;
                DROP POLICY IF EXISTS tenant_isolation ON inventory.warehouses;

                ALTER TABLE ordering.order_lines DISABLE ROW LEVEL SECURITY;
                ALTER TABLE ordering.orders DISABLE ROW LEVEL SECURITY;
                ALTER TABLE imports.import_rows DISABLE ROW LEVEL SECURITY;
                ALTER TABLE imports.import_batches DISABLE ROW LEVEL SECURITY;
                ALTER TABLE marketplace.offers DISABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_reservations DISABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_movements DISABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_levels DISABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.warehouses DISABLE ROW LEVEL SECURITY;

                DROP FUNCTION IF EXISTS infra.current_org();
                """);
            // Grants and the role itself are left in place: dropping a role that other databases may use is unsafe.
        }
    }
}
