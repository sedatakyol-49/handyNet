using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosFiscalLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fiscal_client_id",
                schema: "pos",
                table: "receipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_error",
                schema: "pos",
                table: "receipts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "fiscal_is_test",
                schema: "pos",
                table: "receipts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_mode",
                schema: "pos",
                table: "receipts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_process_data",
                schema: "pos",
                table: "receipts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_process_type",
                schema: "pos",
                table: "receipts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_public_key",
                schema: "pos",
                table: "receipts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_qr_payload",
                schema: "pos",
                table: "receipts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_signature_algorithm",
                schema: "pos",
                table: "receipts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_time_format",
                schema: "pos",
                table: "receipts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fiscal_configurations",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    simulated_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    simulated_failure_rate_percent = table.Column<int>(type: "integer", nullable: true),
                    simulated_outage = table.Column<bool>(type: "boolean", nullable: true),
                    simulated_algorithm = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    fiskaly_tss_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fiskaly_environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    fiskaly_api_key_protected = table.Column<string>(type: "text", nullable: true),
                    fiskaly_api_secret_protected = table.Column<string>(type: "text", nullable: true),
                    fiskaly_credential_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_configurations", x => x.id);
                    table.CheckConstraint("ck_fiscal_configurations_fiskaly_environment", "fiskaly_environment IS NULL OR fiskaly_environment IN ('Sandbox', 'Production')");
                    table.CheckConstraint("ck_fiscal_configurations_org_mode", "register_id IS NOT NULL OR mode IS NOT NULL");
                    table.CheckConstraint("ck_fiscal_configurations_simulated", "(simulated_latency_ms IS NULL OR simulated_latency_ms BETWEEN 0 AND 30000) AND (simulated_failure_rate_percent IS NULL OR simulated_failure_rate_percent BETWEEN 0 AND 100)");
                    table.ForeignKey(
                        name: "fk_fiscal_configurations_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_outages",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_outages", x => x.id);
                    table.CheckConstraint("ck_fiscal_outages_period", "ended_at IS NULL OR ended_at >= started_at");
                    table.ForeignKey(
                        name: "fk_fiscal_outages_cash_sessions_organization_id_session_id",
                        columns: x => new { x.organization_id, x.session_id },
                        principalSchema: "pos",
                        principalTable: "cash_sessions",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fiscal_outages_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_records",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transaction_number = table.Column<long>(type: "bigint", nullable: true),
                    signature_counter = table.Column<long>(type: "bigint", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_fiscal_records_cash_sessions_organization_id_session_id",
                        columns: x => new { x.organization_id, x.session_id },
                        principalSchema: "pos",
                        principalTable: "cash_sessions",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fiscal_records_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "simulated_tses",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    public_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    signature_algorithm = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    time_format = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    transaction_counter = table.Column<long>(type: "bigint", nullable: false),
                    signature_counter = table.Column<long>(type: "bigint", nullable: false),
                    protected_private_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulated_tses", x => x.id);
                    table.UniqueConstraint("ak_simulated_tses_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_simulated_tses_counters", "transaction_counter >= 0 AND signature_counter >= 0");
                });

            migrationBuilder.CreateTable(
                name: "simulated_tse_transactions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_key = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    start_signature_counter = table.Column<long>(type: "bigint", nullable: false),
                    start_signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    process_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    process_data = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    signature_counter = table.Column<long>(type: "bigint", nullable: true),
                    signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulated_tse_transactions", x => x.id);
                    table.CheckConstraint("ck_simulated_tse_transactions_finished", "state = 'Active' OR (finished_at IS NOT NULL AND process_type IS NOT NULL AND process_data IS NOT NULL AND signature IS NOT NULL AND signature_counter > start_signature_counter)");
                    table.CheckConstraint("ck_simulated_tse_transactions_number", "number >= 1 AND start_signature_counter >= 1");
                    table.ForeignKey(
                        name: "fk_simulated_tse_transactions_simulated_tses_organization_id_t",
                        columns: x => new { x.organization_id, x.tse_id },
                        principalSchema: "pos",
                        principalTable: "simulated_tses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_configurations_scope",
                schema: "pos",
                table: "fiscal_configurations",
                columns: new[] { "organization_id", "register_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_outages_organization_id_register_id",
                schema: "pos",
                table: "fiscal_outages",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_outages_organization_id_session_id",
                schema: "pos",
                table: "fiscal_outages",
                columns: new[] { "organization_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_outages_organization_id_started_at",
                schema: "pos",
                table: "fiscal_outages",
                columns: new[] { "organization_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_outages_one_open_per_register",
                schema: "pos",
                table: "fiscal_outages",
                column: "register_id",
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_records_kind_reference_id",
                schema: "pos",
                table: "fiscal_records",
                columns: new[] { "kind", "reference_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_records_organization_id_register_id",
                schema: "pos",
                table: "fiscal_records",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_records_organization_id_session_id",
                schema: "pos",
                table: "fiscal_records",
                columns: new[] { "organization_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_simulated_tse_transactions_organization_id_tse_id",
                schema: "pos",
                table: "simulated_tse_transactions",
                columns: new[] { "organization_id", "tse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_simulated_tse_transactions_tse_id_number",
                schema: "pos",
                table: "simulated_tse_transactions",
                columns: new[] { "tse_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_simulated_tse_transactions_tse_id_transaction_key",
                schema: "pos",
                table: "simulated_tse_transactions",
                columns: new[] { "tse_id", "transaction_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_simulated_tses_organization_id",
                schema: "pos",
                table: "simulated_tses",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_simulated_tses_serial_number",
                schema: "pos",
                table: "simulated_tses",
                column: "serial_number",
                unique: true);

            // ---- Grants: configuration and TSE state are updatable (never deleted); signatures of other events are append-only
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON pos.fiscal_configurations, pos.simulated_tses, pos.simulated_tse_transactions, pos.fiscal_outages
                    TO handynet_app;
                REVOKE DELETE, TRUNCATE ON pos.fiscal_configurations, pos.simulated_tses, pos.simulated_tse_transactions, pos.fiscal_outages
                    FROM handynet_app;
                GRANT SELECT, INSERT ON pos.fiscal_records TO handynet_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON pos.fiscal_records FROM handynet_app;
                """);

            // ---- Row-Level Security ----------------------------------------------------------------------------------------------
            foreach (var table in new[] { "fiscal_configurations", "simulated_tses", "simulated_tse_transactions", "fiscal_outages", "fiscal_records" })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE pos.{table} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON pos.{table}
                        USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                    """);
            }

            // ---- Integrity of TSE data, enforced even for the owner role -----------------------------------------------------------
            // A TSE keeps its identity and key; its counters never go back. A TSE transaction is finished at most once and its
            // start data never changes. An outage can only be ended (once). Signatures of other events never change. Receipts
            // (incl. the new fiscal columns) stay protected by the existing receipts_immutable trigger.
            migrationBuilder.Sql("""
                CREATE FUNCTION pos.guard_simulated_tse() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'UPDATE' THEN
                        RAISE EXCEPTION 'simulated TSEs are never deleted' USING ERRCODE = '42501';
                    END IF;
                    IF NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.serial_number <> OLD.serial_number
                       OR NEW.public_key <> OLD.public_key OR NEW.signature_algorithm <> OLD.signature_algorithm OR NEW.time_format <> OLD.time_format
                       OR NEW.protected_private_key <> OLD.protected_private_key OR NEW.created_at <> OLD.created_at THEN
                        RAISE EXCEPTION 'the identity and key of a TSE cannot change' USING ERRCODE = '42501';
                    END IF;
                    IF NEW.transaction_counter < OLD.transaction_counter OR NEW.signature_counter < OLD.signature_counter THEN
                        RAISE EXCEPTION 'TSE counters never decrease' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER simulated_tses_guard BEFORE UPDATE OR DELETE ON pos.simulated_tses
                    FOR EACH ROW EXECUTE FUNCTION pos.guard_simulated_tse();
                CREATE TRIGGER simulated_tses_no_truncate BEFORE TRUNCATE ON pos.simulated_tses
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

                CREATE FUNCTION pos.guard_simulated_tse_transaction() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'UPDATE' OR OLD.state <> 'Active' OR NEW.state <> 'Finished' THEN
                        RAISE EXCEPTION 'a TSE transaction is only finished once and never deleted (%)', TG_OP USING ERRCODE = '42501';
                    END IF;
                    IF NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.tse_id <> OLD.tse_id
                       OR NEW.transaction_key <> OLD.transaction_key OR NEW.client_id <> OLD.client_id OR NEW.number <> OLD.number
                       OR NEW.started_at <> OLD.started_at OR NEW.start_signature_counter <> OLD.start_signature_counter
                       OR NEW.start_signature <> OLD.start_signature THEN
                        RAISE EXCEPTION 'the start of a TSE transaction cannot change' USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER simulated_tse_transactions_guard BEFORE UPDATE OR DELETE ON pos.simulated_tse_transactions
                    FOR EACH ROW EXECUTE FUNCTION pos.guard_simulated_tse_transaction();
                CREATE TRIGGER simulated_tse_transactions_no_truncate BEFORE TRUNCATE ON pos.simulated_tse_transactions
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

                CREATE FUNCTION pos.guard_fiscal_outage() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'UPDATE' OR OLD.ended_at IS NOT NULL OR NEW.ended_at IS NULL
                       OR NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.register_id <> OLD.register_id
                       OR NEW.session_id IS DISTINCT FROM OLD.session_id OR NEW.mode <> OLD.mode OR NEW.started_at <> OLD.started_at
                       OR NEW.reason <> OLD.reason THEN
                        RAISE EXCEPTION 'a TSE outage can only be ended, once (%)', TG_OP USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER fiscal_outages_guard BEFORE UPDATE OR DELETE ON pos.fiscal_outages
                    FOR EACH ROW EXECUTE FUNCTION pos.guard_fiscal_outage();
                CREATE TRIGGER fiscal_outages_no_truncate BEFORE TRUNCATE ON pos.fiscal_outages
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

                CREATE TRIGGER fiscal_records_immutable BEFORE UPDATE OR DELETE ON pos.fiscal_records
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER fiscal_records_no_truncate BEFORE TRUNCATE ON pos.fiscal_records
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the tables drops their triggers and policies; the functions go separately.
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS pos.fiscal_records, pos.fiscal_outages, pos.simulated_tse_transactions, pos.simulated_tses, pos.fiscal_configurations;
                DROP FUNCTION IF EXISTS pos.guard_simulated_tse();
                DROP FUNCTION IF EXISTS pos.guard_simulated_tse_transaction();
                DROP FUNCTION IF EXISTS pos.guard_fiscal_outage();
                """);

            migrationBuilder.DropColumn(
                name: "fiscal_client_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_error",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_is_test",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_mode",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_process_data",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_process_type",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_public_key",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_qr_payload",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_signature_algorithm",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "fiscal_time_format",
                schema: "pos",
                table: "receipts");
        }
    }
}
