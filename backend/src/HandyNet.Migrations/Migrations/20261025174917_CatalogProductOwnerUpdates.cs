using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <summary>
    /// ADR-0015: canonical products may be changed only by the organization that created them. Row-Level Security on
    /// <c>catalog.products</c>: everyone reads (shared catalog); a tenant session inserts only products created by itself
    /// and updates only products it created (and cannot hand them to another organization). Sessions without a tenant
    /// (platform administration, migrations, background jobs) are unrestricted, like on <c>catalog.product_images</c>.
    /// No DELETE grant (unchanged).
    /// </summary>
    public partial class CatalogProductOwnerUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE catalog.products ENABLE ROW LEVEL SECURITY;
                CREATE POLICY read_all ON catalog.products FOR SELECT USING (true);
                CREATE POLICY creator_inserts ON catalog.products FOR INSERT
                    WITH CHECK (infra.current_org() IS NULL OR created_by_organization_id = infra.current_org());
                CREATE POLICY creator_updates ON catalog.products FOR UPDATE
                    USING (infra.current_org() IS NULL OR created_by_organization_id = infra.current_org())
                    WITH CHECK (infra.current_org() IS NULL OR created_by_organization_id = infra.current_org());
                REVOKE DELETE, TRUNCATE ON catalog.products FROM handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS creator_updates ON catalog.products;
                DROP POLICY IF EXISTS creator_inserts ON catalog.products;
                DROP POLICY IF EXISTS read_all ON catalog.products;
                ALTER TABLE catalog.products DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
