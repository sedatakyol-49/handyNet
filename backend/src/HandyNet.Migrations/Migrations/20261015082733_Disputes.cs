using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Disputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "disputes");

            migrationBuilder.CreateTable(
                name: "disputes",
                schema: "disputes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disputes", x => x.id);
                    table.ForeignKey(
                        name: "fk_disputes_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispute_messages",
                schema: "disputes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dispute_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_dispute_messages_disputes_dispute_id",
                        column: x => x.dispute_id,
                        principalSchema: "disputes",
                        principalTable: "disputes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dispute_messages_dispute_id_sent_at",
                schema: "disputes",
                table: "dispute_messages",
                columns: new[] { "dispute_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_disputes_status",
                schema: "disputes",
                table: "disputes",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_disputes_one_active_per_order",
                schema: "disputes",
                table: "disputes",
                column: "order_id",
                unique: true,
                filter: "status IN ('Open', 'Escalated')");

            // Both parties read and update their dispute; only the buyer opens one. Messages are append-only.
            // Admin access goes through two narrow SECURITY DEFINER functions (summary list, seller lookup).
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA disputes TO handynet_app;
                GRANT SELECT, INSERT, UPDATE ON disputes.disputes TO handynet_app;
                GRANT SELECT, INSERT ON disputes.dispute_messages TO handynet_app;

                ALTER TABLE disputes.disputes ENABLE ROW LEVEL SECURITY;
                CREATE POLICY dispute_parties ON disputes.disputes FOR SELECT
                    USING (buyer_organization_id = infra.current_org() OR seller_organization_id = infra.current_org());
                CREATE POLICY buyer_opens ON disputes.disputes FOR INSERT
                    WITH CHECK (buyer_organization_id = infra.current_org());
                CREATE POLICY parties_update ON disputes.disputes FOR UPDATE
                    USING (buyer_organization_id = infra.current_org() OR seller_organization_id = infra.current_org())
                    WITH CHECK (buyer_organization_id = infra.current_org() OR seller_organization_id = infra.current_org());

                ALTER TABLE disputes.dispute_messages ENABLE ROW LEVEL SECURITY;
                CREATE POLICY dispute_parties ON disputes.dispute_messages
                    USING (EXISTS (SELECT 1 FROM disputes.disputes d WHERE d.id = dispute_id))
                    WITH CHECK (EXISTS (SELECT 1 FROM disputes.disputes d WHERE d.id = dispute_id));

                CREATE FUNCTION disputes.escalated_disputes(p_limit integer)
                    RETURNS TABLE (id uuid, order_id uuid, buyer_organization_id uuid, seller_organization_id uuid, reason text, escalated_at timestamptz)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT d.id, d.order_id, d.buyer_organization_id, d.seller_organization_id, d.reason::text, d.escalated_at
                    FROM disputes.disputes d WHERE d.status = 'Escalated' ORDER BY d.escalated_at LIMIT p_limit
                $$;

                CREATE FUNCTION disputes.dispute_seller(p_id uuid) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$ SELECT d.seller_organization_id FROM disputes.disputes d WHERE d.id = p_id $$;

                REVOKE ALL ON FUNCTION disputes.escalated_disputes(integer) FROM PUBLIC;
                REVOKE ALL ON FUNCTION disputes.dispute_seller(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION disputes.escalated_disputes(integer) TO handynet_app;
                GRANT EXECUTE ON FUNCTION disputes.dispute_seller(uuid) TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS disputes.dispute_seller(uuid);
                DROP FUNCTION IF EXISTS disputes.escalated_disputes(integer);
                """);
            migrationBuilder.DropTable(
                name: "dispute_messages",
                schema: "disputes");

            migrationBuilder.DropTable(
                name: "disputes",
                schema: "disputes");
        }
    }
}
