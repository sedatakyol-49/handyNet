using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosPricelists</c> (ADR-0020): tenant isolation and grants of <c>pos.pricelists</c> (never
/// deleted: customers and receipts reference them) and <c>pos.pricelist_items</c> (configuration: DELETE allowed, audited).
/// </summary>
internal static class PosPricelistsSql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.pricelists ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.pricelists;
            CREATE POLICY tenant_isolation ON pos.pricelists
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON pos.pricelists TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.pricelists FROM handynet_app;

            ALTER TABLE pos.pricelist_items ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.pricelist_items;
            CREATE POLICY tenant_isolation ON pos.pricelist_items
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE, DELETE ON pos.pricelist_items TO handynet_app;
            REVOKE TRUNCATE ON pos.pricelist_items FROM handynet_app;
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP POLICY IF EXISTS tenant_isolation ON pos.pricelist_items;
            DROP POLICY IF EXISTS tenant_isolation ON pos.pricelists;
            """);
}
