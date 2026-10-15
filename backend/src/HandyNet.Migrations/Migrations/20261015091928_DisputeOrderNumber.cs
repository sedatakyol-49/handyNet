using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class DisputeOrderNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "order_number",
                schema: "disputes",
                table: "disputes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            // Backfill from the order, and return the order number from the admin queue function
            // (a changed result type requires DROP + CREATE).
            migrationBuilder.Sql("""
                UPDATE disputes.disputes d SET order_number = 'B2B-' || lpad(o.number::text, 6, '0')
                FROM ordering.orders o WHERE o.id = d.order_id AND d.order_number = '';

                DROP FUNCTION disputes.escalated_disputes(integer);
                CREATE FUNCTION disputes.escalated_disputes(p_limit integer)
                    RETURNS TABLE (id uuid, order_id uuid, order_number text, buyer_organization_id uuid, seller_organization_id uuid,
                                   reason text, escalated_at timestamptz)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT d.id, d.order_id, d.order_number::text, d.buyer_organization_id, d.seller_organization_id, d.reason::text, d.escalated_at
                    FROM disputes.disputes d WHERE d.status = 'Escalated' ORDER BY d.escalated_at LIMIT p_limit
                $$;
                REVOKE ALL ON FUNCTION disputes.escalated_disputes(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION disputes.escalated_disputes(integer) TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION disputes.escalated_disputes(integer);
                CREATE FUNCTION disputes.escalated_disputes(p_limit integer)
                    RETURNS TABLE (id uuid, order_id uuid, buyer_organization_id uuid, seller_organization_id uuid, reason text, escalated_at timestamptz)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT d.id, d.order_id, d.buyer_organization_id, d.seller_organization_id, d.reason::text, d.escalated_at
                    FROM disputes.disputes d WHERE d.status = 'Escalated' ORDER BY d.escalated_at LIMIT p_limit
                $$;
                REVOKE ALL ON FUNCTION disputes.escalated_disputes(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION disputes.escalated_disputes(integer) TO handynet_app;
                """);
            migrationBuilder.DropColumn(
                name: "order_number",
                schema: "disputes",
                table: "disputes");
        }
    }
}
