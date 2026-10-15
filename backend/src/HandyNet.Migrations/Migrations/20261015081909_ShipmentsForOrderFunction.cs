using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ShipmentsForOrderFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Id-only lookup for the outbox handler that marks Manual shipments delivered (ADR-0005).
            migrationBuilder.Sql("""
                CREATE FUNCTION shipping.shipments_for_order(p_order_id uuid)
                    RETURNS TABLE (id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$ SELECT s.id FROM shipping.shipments s WHERE s.order_id = p_order_id $$;
                REVOKE ALL ON FUNCTION shipping.shipments_for_order(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION shipping.shipments_for_order(uuid) TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS shipping.shipments_for_order(uuid);");
        }
    }
}
