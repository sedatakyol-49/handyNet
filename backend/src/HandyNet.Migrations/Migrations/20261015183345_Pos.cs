using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Pos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pos");

            migrationBuilder.CreateTable(
                name: "registers",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_receipt_number = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registers", x => x.id);
                    table.UniqueConstraint("ak_registers_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_registers_next_number", "next_receipt_number >= 1");
                    table.ForeignKey(
                        name: "fk_registers_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_sessions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opening_cash_minor = table.Column<long>(type: "bigint", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_cash_minor = table.Column<long>(type: "bigint", nullable: true),
                    counted_cash_minor = table.Column<long>(type: "bigint", nullable: true),
                    difference_minor = table.Column<long>(type: "bigint", nullable: true),
                    closing_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_sessions", x => x.id);
                    table.UniqueConstraint("ak_cash_sessions_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_cash_sessions_closed", "status = 'Open' OR (closed_at IS NOT NULL AND closed_by IS NOT NULL AND summary IS NOT NULL AND counted_cash_minor >= 0 AND expected_cash_minor IS NOT NULL AND difference_minor = counted_cash_minor - expected_cash_minor)");
                    table.CheckConstraint("ck_cash_sessions_opening", "opening_cash_minor >= 0");
                    table.ForeignKey(
                        name: "fk_cash_sessions_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_movements",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_movements", x => x.id);
                    table.CheckConstraint("ck_cash_movements_amount", "amount_minor > 0");
                    table.ForeignKey(
                        name: "fk_cash_movements_cash_sessions_organization_id_session_id",
                        columns: x => new { x.organization_id, x.session_id },
                        principalSchema: "pos",
                        principalTable: "cash_sessions",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    referenced_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    total_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    change_minor = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    fiscal_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fiscal_notice = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fiscal_tse_serial_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fiscal_transaction_number = table.Column<long>(type: "bigint", nullable: true),
                    fiscal_signature_counter = table.Column<long>(type: "bigint", nullable: true),
                    fiscal_signature = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    fiscal_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fiscal_finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipts", x => x.id);
                    table.UniqueConstraint("ak_receipts_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_receipts_kind", "(kind = 'Sale' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL) OR (kind IN ('Storno', 'Return') AND total_gross_minor <= 0 AND change_minor = 0 AND referenced_receipt_id IS NOT NULL)");
                    table.CheckConstraint("ck_receipts_number", "number >= 1");
                    table.CheckConstraint("ck_receipts_totals", "total_net_minor + total_vat_minor = total_gross_minor AND change_minor >= 0");
                    table.ForeignKey(
                        name: "fk_receipts_cash_sessions_organization_id_session_id",
                        columns: x => new { x.organization_id, x.session_id },
                        principalSchema: "pos",
                        principalTable: "cash_sessions",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipts_receipts_organization_id_referenced_receipt_id",
                        columns: x => new { x.organization_id, x.referenced_receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipts_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipts_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_lines",
                schema: "pos",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imei = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    discount_minor = table.Column<long>(type: "bigint", nullable: false),
                    line_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_treatment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    vat_rate_basis_points = table.Column<int>(type: "integer", nullable: false),
                    vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    net_minor = table.Column<long>(type: "bigint", nullable: false),
                    purchase_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    original_line_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_lines", x => new { x.receipt_id, x.line_number });
                    table.CheckConstraint("ck_receipt_lines_amounts", "line_gross_minor = unit_price_minor * quantity - discount_minor AND net_minor + vat_minor = line_gross_minor");
                    table.CheckConstraint("ck_receipt_lines_margin", "tax_treatment <> 'Margin25a' OR (stock_unit_id IS NOT NULL AND purchase_price_minor IS NOT NULL)");
                    table.CheckConstraint("ck_receipt_lines_quantity", "quantity <> 0 AND unit_price_minor > 0");
                    table.ForeignKey(
                        name: "fk_receipt_lines_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_lines_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_lines_stock_units_stock_unit_id",
                        column: x => x.stock_unit_id,
                        principalSchema: "inventory",
                        principalTable: "stock_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_payments",
                schema: "pos",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    tendered_minor = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_payments", x => new { x.receipt_id, x.method });
                    table.CheckConstraint("ck_receipt_payments_amount", "amount_minor <> 0 AND (tendered_minor IS NULL OR tendered_minor >= amount_minor)");
                    table.ForeignKey(
                        name: "fk_receipt_payments_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_organization_id_session_id",
                schema: "pos",
                table: "cash_movements",
                columns: new[] { "organization_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_sessions_organization_id_register_id_opened_at",
                schema: "pos",
                table: "cash_sessions",
                columns: new[] { "organization_id", "register_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_cash_sessions_one_open_per_register",
                schema: "pos",
                table: "cash_sessions",
                column: "register_id",
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_lines_organization_id_receipt_id",
                schema: "pos",
                table: "receipt_lines",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_lines_product_id",
                schema: "pos",
                table: "receipt_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_lines_stock_unit_id",
                schema: "pos",
                table: "receipt_lines",
                column: "stock_unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_payments_organization_id_receipt_id",
                schema: "pos",
                table: "receipt_payments",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_created_at",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_idempotency_key",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_referenced_receipt_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "referenced_receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_register_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_session_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_warehouse_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_register_id_number",
                schema: "pos",
                table: "receipts",
                columns: new[] { "register_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_receipts_one_storno",
                schema: "pos",
                table: "receipts",
                column: "referenced_receipt_id",
                unique: true,
                filter: "kind = 'Storno'");

            migrationBuilder.CreateIndex(
                name: "ix_registers_organization_id_name",
                schema: "pos",
                table: "registers",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_registers_organization_id_warehouse_id",
                schema: "pos",
                table: "registers",
                columns: new[] { "organization_id", "warehouse_id" });

            // ---- Grants: registers and sessions are updatable; receipts and cash movements are insert-only (GoBD) -----------
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA pos TO handynet_app;
                GRANT SELECT, INSERT, UPDATE ON pos.registers, pos.cash_sessions TO handynet_app;
                GRANT SELECT, INSERT ON pos.receipts, pos.receipt_lines, pos.receipt_payments, pos.cash_movements TO handynet_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON pos.receipts, pos.receipt_lines, pos.receipt_payments, pos.cash_movements FROM handynet_app;
                REVOKE DELETE, TRUNCATE ON pos.registers, pos.cash_sessions FROM handynet_app;
                """);

            // ---- Row-Level Security: every table carries organization_id -----------------------------------------------------
            foreach (var table in new[] { "registers", "cash_sessions", "cash_movements", "receipts", "receipt_lines", "receipt_payments" })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE pos.{table} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON pos.{table}
                        USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                    """);
            }

            // ---- Immutability even for the owner role: receipts, their lines and payments, cash movements, closed sessions ---
            migrationBuilder.Sql("""
                CREATE FUNCTION pos.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'pos % rows are immutable (%)', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END $$;
                CREATE TRIGGER receipts_immutable BEFORE UPDATE OR DELETE ON pos.receipts
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER receipt_lines_immutable BEFORE UPDATE OR DELETE ON pos.receipt_lines
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER receipt_payments_immutable BEFORE UPDATE OR DELETE ON pos.receipt_payments
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER cash_movements_immutable BEFORE UPDATE OR DELETE ON pos.cash_movements
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER receipts_no_truncate BEFORE TRUNCATE ON pos.receipts
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER receipt_lines_no_truncate BEFORE TRUNCATE ON pos.receipt_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER receipt_payments_no_truncate BEFORE TRUNCATE ON pos.receipt_payments
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER cash_movements_no_truncate BEFORE TRUNCATE ON pos.cash_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

                -- A closed session (Z-report) is final; sessions are never deleted.
                CREATE FUNCTION pos.protect_session() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' OR OLD.status = 'Closed' THEN
                        RAISE EXCEPTION 'closed cash sessions are immutable (%)', TG_OP USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER cash_sessions_final BEFORE UPDATE OR DELETE ON pos.cash_sessions
                    FOR EACH ROW EXECUTE FUNCTION pos.protect_session();
                """);

            // ---- Gapless receipt numbers, enforced by the database -------------------------------------------------------------
            // 1. The counter only ever moves by +1.
            // 2. At commit, every number taken from the counter must have its receipt (deferred constraint trigger), so a
            //    transaction that allocates a number without writing the receipt fails instead of leaving a gap.
            // 3. A receipt must carry exactly the number just allocated (the register row stays locked until commit) and
            //    belong to an open session of its register.
            migrationBuilder.Sql("""
                CREATE FUNCTION pos.guard_register() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.next_receipt_number NOT IN (OLD.next_receipt_number, OLD.next_receipt_number + 1) THEN
                        RAISE EXCEPTION 'receipt numbers advance by one (% -> %)', OLD.next_receipt_number, NEW.next_receipt_number
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW.organization_id <> OLD.organization_id OR NEW.warehouse_id <> OLD.warehouse_id THEN
                        RAISE EXCEPTION 'a register cannot move to another organization or warehouse' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER registers_guard BEFORE UPDATE ON pos.registers
                    FOR EACH ROW EXECUTE FUNCTION pos.guard_register();

                CREATE FUNCTION pos.check_number_used() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.next_receipt_number > OLD.next_receipt_number AND NOT EXISTS (
                        SELECT 1 FROM pos.receipts r WHERE r.register_id = NEW.id AND r.number = NEW.next_receipt_number - 1) THEN
                        RAISE EXCEPTION 'receipt number % of register % was allocated but not used (gap)', NEW.next_receipt_number - 1, NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER registers_number_used AFTER UPDATE OF next_receipt_number ON pos.registers
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_number_used();

                CREATE FUNCTION pos.check_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pos.registers g WHERE g.id = NEW.register_id AND g.next_receipt_number = NEW.number + 1) THEN
                        RAISE EXCEPTION 'receipt number % was not allocated from register %', NEW.number, NEW.register_id USING ERRCODE = '23514';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pos.cash_sessions s
                                   WHERE s.id = NEW.session_id AND s.register_id = NEW.register_id AND s.status = 'Open') THEN
                        RAISE EXCEPTION 'a receipt needs an open cash session of its register' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER receipts_check BEFORE INSERT ON pos.receipts
                    FOR EACH ROW EXECUTE FUNCTION pos.check_receipt();

                CREATE FUNCTION pos.check_movement() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pos.cash_sessions s WHERE s.id = NEW.session_id AND s.status = 'Open') THEN
                        RAISE EXCEPTION 'cash movements need an open cash session' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER cash_movements_check BEFORE INSERT ON pos.cash_movements
                    FOR EACH ROW EXECUTE FUNCTION pos.check_movement();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_movements",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "receipt_lines",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "receipt_payments",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "receipts",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "cash_sessions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "registers",
                schema: "pos");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS pos.check_movement();
                DROP FUNCTION IF EXISTS pos.check_receipt();
                DROP FUNCTION IF EXISTS pos.check_number_used();
                DROP FUNCTION IF EXISTS pos.guard_register();
                DROP FUNCTION IF EXISTS pos.protect_session();
                DROP FUNCTION IF EXISTS pos.forbid_change();
                """);
        }
    }
}
