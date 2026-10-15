using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ShippingAndOrderDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shipping");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                schema: "ordering",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                schema: "ordering",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inspection_ends_at",
                schema: "ordering",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shipments",
                schema: "shipping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tracking_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    weight_grams = table.Column<int>(type: "integer", nullable: false),
                    length_cm = table.Column<int>(type: "integer", nullable: false),
                    width_cm = table.Column<int>(type: "integer", nullable: false),
                    height_cm = table.Column<int>(type: "integer", nullable: false),
                    declared_value_minor = table.Column<long>(type: "bigint", nullable: false),
                    insured = table.Column<bool>(type: "boolean", nullable: false),
                    contains_lithium_batteries = table.Column<bool>(type: "boolean", nullable: false),
                    label_pdf = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    handed_over_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipments", x => x.id);
                    table.CheckConstraint("ck_shipments_parcel", "weight_grams > 0 AND length_cm > 0 AND width_cm > 0 AND height_cm > 0");
                    table.CheckConstraint("ck_shipments_value", "declared_value_minor >= 0");
                    table.ForeignKey(
                        name: "fk_shipments_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tracking_events",
                schema: "shipping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracking_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_tracking_events_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipping",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_active",
                schema: "shipping",
                table: "shipments",
                column: "status",
                filter: "status IN ('LabelCreated', 'HandedOver', 'InTransit', 'Exception')");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_carrier_tracking_number",
                schema: "shipping",
                table: "shipments",
                columns: new[] { "carrier", "tracking_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shipments_order_id",
                schema: "shipping",
                table: "shipments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracking_events_shipment_id_external_id",
                schema: "shipping",
                table: "tracking_events",
                columns: new[] { "shipment_id", "external_id" },
                unique: true);

            // Grants, RLS (seller and buyer see the shipment; only the seller writes) and the two
            // id-only SECURITY DEFINER functions used by background workers to find due work across tenants.
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA shipping TO handynet_app;
                GRANT SELECT, INSERT, UPDATE ON shipping.shipments TO handynet_app;
                GRANT SELECT, INSERT ON shipping.tracking_events TO handynet_app;

                ALTER TABLE shipping.shipments ENABLE ROW LEVEL SECURITY;
                CREATE POLICY shipment_parties ON shipping.shipments FOR SELECT
                    USING (seller_organization_id = infra.current_org() OR buyer_organization_id = infra.current_org());
                CREATE POLICY seller_writes ON shipping.shipments FOR INSERT
                    WITH CHECK (seller_organization_id = infra.current_org());
                CREATE POLICY seller_updates ON shipping.shipments FOR UPDATE
                    USING (seller_organization_id = infra.current_org()) WITH CHECK (seller_organization_id = infra.current_org());

                ALTER TABLE shipping.tracking_events ENABLE ROW LEVEL SECURITY;
                CREATE POLICY shipment_parties ON shipping.tracking_events
                    USING (EXISTS (SELECT 1 FROM shipping.shipments s WHERE s.id = shipment_id))
                    WITH CHECK (EXISTS (SELECT 1 FROM shipping.shipments s WHERE s.id = shipment_id
                                        AND s.seller_organization_id = infra.current_org()));

                CREATE FUNCTION shipping.shipments_due_for_tracking(p_min_age_minutes integer, p_limit integer)
                    RETURNS TABLE (id uuid, seller_organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT s.id, s.seller_organization_id
                    FROM shipping.shipments s
                    WHERE s.status IN ('HandedOver', 'InTransit', 'Exception', 'LabelCreated')
                      AND s.carrier <> 'Manual'
                      AND (s.last_synced_at IS NULL OR s.last_synced_at <= now() - make_interval(mins => p_min_age_minutes))
                    ORDER BY s.last_synced_at NULLS FIRST
                    LIMIT p_limit
                $$;

                CREATE FUNCTION ordering.orders_due_for_completion(p_limit integer)
                    RETURNS TABLE (id uuid, seller_organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT o.id, o.seller_organization_id
                    FROM ordering.orders o
                    WHERE o.status = 'Delivered' AND o.inspection_ends_at <= now()
                    ORDER BY o.inspection_ends_at
                    LIMIT p_limit
                $$;

                REVOKE ALL ON FUNCTION shipping.shipments_due_for_tracking(integer, integer) FROM PUBLIC;
                REVOKE ALL ON FUNCTION ordering.orders_due_for_completion(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION shipping.shipments_due_for_tracking(integer, integer) TO handynet_app;
                GRANT EXECUTE ON FUNCTION ordering.orders_due_for_completion(integer) TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS ordering.orders_due_for_completion(integer);
                DROP FUNCTION IF EXISTS shipping.shipments_due_for_tracking(integer, integer);
                """);

            migrationBuilder.DropTable(
                name: "tracking_events",
                schema: "shipping");

            migrationBuilder.DropTable(
                name: "shipments",
                schema: "shipping");

            migrationBuilder.DropColumn(
                name: "completed_at",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "inspection_ends_at",
                schema: "ordering",
                table: "orders");
        }
    }
}
