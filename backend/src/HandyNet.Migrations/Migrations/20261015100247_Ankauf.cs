using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Ankauf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ankauf");

            migrationBuilder.CreateTable(
                name: "condition_policies",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_condition_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_lists",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    file_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    sheet_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    skipped_rows = table.Column<string>(type: "jsonb", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_lists", x => x.id);
                    table.CheckConstraint("ck_price_lists_validity", "valid_until > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "price_list_entries",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    model_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    previous_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    change = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_list_entries", x => x.id);
                    table.CheckConstraint("ck_price_list_entries_price", "price_minor > 0");
                    table.ForeignKey(
                        name: "fk_price_list_entries_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalSchema: "ankauf",
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_versions",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_versions", x => x.id);
                    table.CheckConstraint("ck_price_versions_price", "price_minor > 0");
                    table.CheckConstraint("ck_price_versions_validity", "valid_until > valid_from");
                    table.ForeignKey(
                        name: "fk_price_versions_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalSchema: "ankauf",
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "ankauf",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    model_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    price_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    condition_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    condition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    defects = table.Column<string[]>(type: "text[]", nullable: false),
                    battery_health_percent = table.Column<int>(type: "integer", nullable: true),
                    imei = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: true),
                    reference_gut_minor = table.Column<long>(type: "bigint", nullable: false),
                    condition_adjustment = table.Column<int>(type: "integer", nullable: false),
                    total_deduction = table.Column<int>(type: "integer", nullable: false),
                    reference_purchase_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    platform_offer_minor = table.Column<long>(type: "bigint", nullable: false),
                    max_consumer_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    consumer_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    dealer_margin_minor = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotes", x => x.id);
                    table.CheckConstraint("ck_quotes_amounts", "reference_purchase_price_minor >= 0 AND platform_offer_minor >= 0 AND max_consumer_price_minor >= 0");
                    table.ForeignKey(
                        name: "fk_quotes_condition_policies_condition_policy_id",
                        column: x => x.condition_policy_id,
                        principalSchema: "ankauf",
                        principalTable: "condition_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quotes_price_versions_price_version_id",
                        column: x => x.price_version_id,
                        principalSchema: "ankauf",
                        principalTable: "price_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_condition_policies_effective_from",
                schema: "ankauf",
                table: "condition_policies",
                column: "effective_from");

            migrationBuilder.CreateIndex(
                name: "ix_condition_policies_number",
                schema: "ankauf",
                table: "condition_policies",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_list_entries_price_list_id_decision",
                schema: "ankauf",
                table: "price_list_entries",
                columns: new[] { "price_list_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "ix_price_list_entries_price_list_id_model_key",
                schema: "ankauf",
                table: "price_list_entries",
                columns: new[] { "price_list_id", "model_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_lists_imported_at",
                schema: "ankauf",
                table: "price_lists",
                column: "imported_at");

            migrationBuilder.CreateIndex(
                name: "ix_price_versions_model_key_valid_from",
                schema: "ankauf",
                table: "price_versions",
                columns: new[] { "model_key", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ix_price_versions_price_list_id_model_key",
                schema: "ankauf",
                table: "price_versions",
                columns: new[] { "price_list_id", "model_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotes_condition_policy_id",
                schema: "ankauf",
                table: "quotes",
                column: "condition_policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_organization_id_created_at",
                schema: "ankauf",
                table: "quotes",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_price_version_id",
                schema: "ankauf",
                table: "quotes",
                column: "price_version_id");

            // Price lists are platform data (admin endpoints only). Price versions, policies and quotes are immutable:
            // the app may insert and read, never change, and triggers block edits even for the owner role.
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA ankauf TO handynet_app;
                GRANT SELECT, INSERT, UPDATE ON ankauf.price_lists, ankauf.price_list_entries TO handynet_app;
                GRANT SELECT, INSERT ON ankauf.price_versions, ankauf.condition_policies, ankauf.quotes TO handynet_app;
                REVOKE UPDATE, DELETE ON ankauf.price_versions, ankauf.condition_policies, ankauf.quotes FROM handynet_app;

                CREATE FUNCTION ankauf.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ankauf % rows are immutable (%)', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END $$;
                CREATE TRIGGER price_versions_immutable BEFORE UPDATE OR DELETE ON ankauf.price_versions
                    FOR EACH ROW EXECUTE FUNCTION ankauf.forbid_change();
                CREATE TRIGGER condition_policies_immutable BEFORE UPDATE OR DELETE ON ankauf.condition_policies
                    FOR EACH ROW EXECUTE FUNCTION ankauf.forbid_change();
                CREATE TRIGGER quotes_immutable BEFORE UPDATE OR DELETE ON ankauf.quotes
                    FOR EACH ROW EXECUTE FUNCTION ankauf.forbid_change();

                ALTER TABLE ankauf.quotes ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON ankauf.quotes
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                """);

            // Policy version 1 = the sample values from the specification. A literal (not PolicySettings.Default) so this
            // migration produces the same data forever, even if the code default changes later.
            migrationBuilder.Sql("""
                INSERT INTO ankauf.condition_policies (id, number, settings, effective_from, created_at, created_by)
                VALUES (gen_random_uuid(), 1, '{
                    "conditionAdjustments": {"Neu": 2000, "WieNeu": 700, "SehrGut": 500, "Gut": 0, "Akzeptabel": -2000},
                    "brandOverrides": {},
                    "modelOverrides": {},
                    "defectDeductions": {"ScreenDamage": 3000, "HousingDamage": 1000, "CameraDefect": 1500, "BiometricDefect": 2000, "MissingFunction": 2500},
                    "batteryHealthThresholdPercent": 80,
                    "batteryDeduction": 1000,
                    "platformMargin": 0,
                    "minDealerMargin": 1500
                }'::jsonb, now(), now(), '00000000-0000-0000-0000-000000000000');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS quotes_immutable ON ankauf.quotes;
                DROP TRIGGER IF EXISTS condition_policies_immutable ON ankauf.condition_policies;
                DROP TRIGGER IF EXISTS price_versions_immutable ON ankauf.price_versions;
                DROP FUNCTION IF EXISTS ankauf.forbid_change();
                """);
            migrationBuilder.DropTable(
                name: "price_list_entries",
                schema: "ankauf");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "ankauf");

            migrationBuilder.DropTable(
                name: "condition_policies",
                schema: "ankauf");

            migrationBuilder.DropTable(
                name: "price_versions",
                schema: "ankauf");

            migrationBuilder.DropTable(
                name: "price_lists",
                schema: "ankauf");
        }
    }
}
