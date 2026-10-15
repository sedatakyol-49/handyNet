using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "infra");

            migrationBuilder.EnsureSchema(
                name: "imports");

            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.EnsureSchema(
                name: "marketplace");

            migrationBuilder.EnsureSchema(
                name: "ordering");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateSequence(
                name: "order_number_seq",
                schema: "ordering",
                startValue: 1000L);

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "infra",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "infra",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    brand_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    quality = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    mpn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    mpn_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ean = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    compatible_models = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_identifier", "ean IS NOT NULL OR mpn_key IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_platform_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "warehouses",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouses", x => x.id);
                    table.UniqueConstraint("ak_warehouses_organization_id_id", x => new { x.organization_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    vat_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    is_wholesaler = table.Column<bool>(type: "boolean", nullable: false),
                    is_retailer = table.Column<bool>(type: "boolean", nullable: false),
                    is_repair_provider = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_has_capability", "is_wholesaler OR is_retailer OR is_repair_provider");
                    table.ForeignKey(
                        name: "fk_organizations_users_created_by",
                        column: x => x.created_by,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_batches",
                schema: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    file_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    headers = table.Column<List<string>>(type: "text[]", nullable: false),
                    mapping = table.Column<string>(type: "jsonb", nullable: false),
                    publish_offers = table.Column<bool>(type: "boolean", nullable: false),
                    sync_stock = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batches_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offers",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sku_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    min_order_quantity = table.Column<int>(type: "integer", nullable: false),
                    delivery_days = table.Column<int>(type: "integer", nullable: false),
                    condition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offers", x => x.id);
                    table.CheckConstraint("ck_offers_delivery_days", "delivery_days BETWEEN 0 AND 60");
                    table.CheckConstraint("ck_offers_min_quantity", "min_order_quantity >= 1");
                    table.CheckConstraint("ck_offers_price", "price_minor > 0");
                    table.ForeignKey(
                        name: "fk_offers_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offers_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_levels",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_levels", x => x.id);
                    table.CheckConstraint("ck_stock_levels_on_hand", "on_hand >= 0");
                    table.CheckConstraint("ck_stock_levels_reserved", "reserved >= 0 AND reserved <= on_hand");
                    table.ForeignKey(
                        name: "fk_stock_levels_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_levels_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    on_hand_after = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.CheckConstraint("ck_stock_movements_nonzero", "quantity <> 0");
                    table.ForeignKey(
                        name: "fk_stock_movements_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reserved_by_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservations", x => x.id);
                    table.CheckConstraint("ck_stock_reservations_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_stock_reservations_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_reservations_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('ordering.order_number_seq')"),
                    buyer_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    net_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_rate_basis_points = table.Column<int>(type: "integer", nullable: false),
                    vat_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    buyer_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    placed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_parties", "buyer_organization_id <> seller_organization_id");
                    table.CheckConstraint("ck_orders_totals", "net_total_minor > 0 AND vat_total_minor >= 0 AND gross_total_minor = net_total_minor + vat_total_minor");
                    table.ForeignKey(
                        name: "fk_orders_organizations_buyer_organization_id",
                        column: x => x.buyer_organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_organizations_seller_organization_id",
                        column: x => x.seller_organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_modules",
                schema: "tenancy",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    enabled_by = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_modules", x => new { x.organization_id, x.module });
                    table.ForeignKey(
                        name: "fk_organization_modules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_rows",
                schema: "imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    raw = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    messages = table.Column<List<string>>(type: "text[]", nullable: false),
                    sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ean = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    mpn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    quality = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    condition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    price_minor = table.Column<long>(type: "bigint", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: true),
                    delivery_days = table.Column<int>(type: "integer", nullable: true),
                    min_order_quantity = table.Column<int>(type: "integer", nullable: true),
                    product_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    old_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    old_quantity = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_rows", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_rows_import_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "imports",
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    product_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    line_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => x.id);
                    table.CheckConstraint("ck_order_lines_amounts", "unit_price_minor > 0 AND line_net_minor = unit_price_minor * quantity");
                    table.CheckConstraint("ck_order_lines_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_order_lines_offers_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "marketplace",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_lines_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_lines_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_lines_stock_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "stock_reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_type_entity_id",
                schema: "infra",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_organization_id_occurred_at",
                schema: "infra",
                table: "audit_log",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_organization_id_created_at",
                schema: "imports",
                table: "import_batches",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_organization_id_file_sha256",
                schema: "imports",
                table: "import_batches",
                columns: new[] { "organization_id", "file_sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_organization_id_warehouse_id",
                schema: "imports",
                table: "import_batches",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_batch_id_row_number",
                schema: "imports",
                table: "import_rows",
                columns: new[] { "batch_id", "row_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memberships_organization_id_user_id",
                schema: "tenancy",
                table: "memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                schema: "tenancy",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offers_organization_id_sku_key",
                schema: "marketplace",
                table: "offers",
                columns: new[] { "organization_id", "sku_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offers_organization_id_warehouse_id",
                schema: "marketplace",
                table: "offers",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offers_product_id_status",
                schema: "marketplace",
                table: "offers",
                columns: new[] { "product_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_offer_id",
                schema: "ordering",
                table: "order_lines",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_order_id_line_number",
                schema: "ordering",
                table: "order_lines",
                columns: new[] { "order_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_product_id",
                schema: "ordering",
                table: "order_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_reservation_id",
                schema: "ordering",
                table: "order_lines",
                column: "reservation_id",
                unique: true,
                filter: "reservation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_buyer_organization_id_idempotency_key",
                schema: "ordering",
                table: "orders",
                columns: new[] { "buyer_organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_buyer_organization_id_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "buyer_organization_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_number",
                schema: "ordering",
                table: "orders",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_seller_organization_id_status_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "seller_organization_id", "status", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_organizations_created_by",
                schema: "tenancy",
                table: "organizations",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_status",
                schema: "tenancy",
                table: "organizations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_vat_id",
                schema: "tenancy",
                table: "organizations",
                column: "vat_id",
                unique: true,
                filter: "vat_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unprocessed",
                schema: "infra",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_products_category",
                schema: "catalog",
                table: "products",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_products_ean",
                schema: "catalog",
                table: "products",
                column: "ean",
                unique: true,
                filter: "ean IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_products_identity",
                schema: "catalog",
                table: "products",
                columns: new[] { "brand_key", "mpn_key", "quality" },
                unique: true,
                filter: "mpn_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_levels_organization_id_product_id",
                schema: "inventory",
                table: "stock_levels",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_levels_organization_id_warehouse_id",
                schema: "inventory",
                table: "stock_levels",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_levels_product_id",
                schema: "inventory",
                table: "stock_levels",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_levels_warehouse_id_product_id",
                schema: "inventory",
                table: "stock_levels",
                columns: new[] { "warehouse_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_organization_id_product_id_occurred_at",
                schema: "inventory",
                table: "stock_movements",
                columns: new[] { "organization_id", "product_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_organization_id_warehouse_id",
                schema: "inventory",
                table: "stock_movements",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_product_id",
                schema: "inventory",
                table: "stock_movements",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_organization_id_status",
                schema: "inventory",
                table: "stock_reservations",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_organization_id_warehouse_id",
                schema: "inventory",
                table: "stock_reservations",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_product_id",
                schema: "inventory",
                table: "stock_reservations",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_reference",
                schema: "inventory",
                table: "stock_reservations",
                column: "reference");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                schema: "identity",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_warehouses_organization_id_code",
                schema: "inventory",
                table: "warehouses",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warehouses_one_default",
                schema: "inventory",
                table: "warehouses",
                column: "organization_id",
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "import_rows",
                schema: "imports");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "organization_modules",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "stock_levels",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_movements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "import_batches",
                schema: "imports");

            migrationBuilder.DropTable(
                name: "offers",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "stock_reservations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "warehouses",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropSequence(
                name: "order_number_seq",
                schema: "ordering");
        }
    }
}
