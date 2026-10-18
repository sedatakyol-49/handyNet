using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosStaffAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fiscal_configurations",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "fiscal_outages",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "fiscal_records",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "simulated_tse_transactions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "simulated_tses",
                schema: "pos");

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

            migrationBuilder.AddColumn<int>(
                name: "auto_lock_minutes",
                schema: "pos",
                table: "registers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "html_body",
                schema: "notifications",
                table: "email_messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    approver_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_for = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cash_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                    table.CheckConstraint("ck_approvals_not_self", "approver_user_id <> requested_by_user_id");
                    table.CheckConstraint("ck_approvals_permissions", "cardinality(permissions) >= 1");
                    table.CheckConstraint("ck_approvals_times", "expires_at > created_at");
                    table.CheckConstraint("ck_approvals_use", "(used_at IS NULL AND used_for IS NULL AND receipt_id IS NULL AND cash_movement_id IS NULL AND session_id IS NULL) OR (used_at IS NOT NULL AND used_for IS NOT NULL AND num_nonnulls(receipt_id, cash_movement_id, session_id) = 1)");
                    table.ForeignKey(
                        name: "fk_approvals_cash_movements_cash_movement_id",
                        column: x => x.cash_movement_id,
                        principalSchema: "pos",
                        principalTable: "cash_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_cash_sessions_organization_id_session_id",
                        columns: x => new { x.organization_id, x.session_id },
                        principalSchema: "pos",
                        principalTable: "cash_sessions",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_users_approver_user_id",
                        column: x => x.approver_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cashier_pins",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashier_pins", x => new { x.organization_id, x.user_id });
                    table.CheckConstraint("ck_cashier_pins_attempts", "failed_attempts BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_cashier_pins_hash", "pin_hash IS NULL OR pin_hash LIKE 'pbkdf2-sha256$%'");
                    table.ForeignKey(
                        name: "fk_cashier_pins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cashier_sessions",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashier_sessions", x => x.id);
                    table.CheckConstraint("ck_cashier_sessions_end", "(ended_at IS NULL) = (ended_reason IS NULL)");
                    table.CheckConstraint("ck_cashier_sessions_times", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_cashier_sessions_registers_organization_id_register_id",
                        columns: x => new { x.organization_id, x.register_id },
                        principalSchema: "pos",
                        principalTable: "registers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cashier_sessions_users_cashier_user_id",
                        column: x => x.cashier_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cashier_sessions_users_device_user_id",
                        column: x => x.device_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_emails",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_emails", x => x.id);
                    table.ForeignKey(
                        name: "fk_receipt_emails_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_links",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_links", x => x.id);
                    table.CheckConstraint("ck_receipt_links_expiry", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_receipt_links_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_registers_auto_lock",
                schema: "pos",
                table: "registers",
                sql: "auto_lock_minutes BETWEEN 1 AND 240");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_approver_user_id",
                schema: "pos",
                table: "approvals",
                column: "approver_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_cash_movement_id",
                schema: "pos",
                table: "approvals",
                column: "cash_movement_id",
                filter: "cash_movement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_organization_id_receipt_id",
                schema: "pos",
                table: "approvals",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_organization_id_register_id",
                schema: "pos",
                table: "approvals",
                columns: new[] { "organization_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_organization_id_session_id",
                schema: "pos",
                table: "approvals",
                columns: new[] { "organization_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_receipt_id",
                schema: "pos",
                table: "approvals",
                column: "receipt_id",
                filter: "receipt_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_session_id",
                schema: "pos",
                table: "approvals",
                column: "session_id",
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_token_hash",
                schema: "pos",
                table: "approvals",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cashier_pins_user_id",
                schema: "pos",
                table: "cashier_pins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_cashier_user_id",
                schema: "pos",
                table: "cashier_sessions",
                column: "cashier_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_device_user_id",
                schema: "pos",
                table: "cashier_sessions",
                column: "device_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_organization_id_register_id_device_user_id",
                schema: "pos",
                table: "cashier_sessions",
                columns: new[] { "organization_id", "register_id", "device_user_id" },
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_sessions_token_hash",
                schema: "pos",
                table: "cashier_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_receipt_emails_organization_id_receipt_id",
                schema: "pos",
                table: "receipt_emails",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_links_organization_id_receipt_id",
                schema: "pos",
                table: "receipt_links",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_links_token_hash",
                schema: "pos",
                table: "receipt_links",
                column: "token_hash",
                unique: true);

            PosStaffAndDocumentsSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosStaffAndDocumentsSql.Down(migrationBuilder);

            migrationBuilder.DropTable(
                name: "approvals",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "cashier_pins",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "cashier_sessions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "receipt_emails",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "receipt_links",
                schema: "pos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registers_auto_lock",
                schema: "pos",
                table: "registers");

            migrationBuilder.DropColumn(
                name: "auto_lock_minutes",
                schema: "pos",
                table: "registers");

            migrationBuilder.DropColumn(
                name: "html_body",
                schema: "notifications",
                table: "email_messages");

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
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    fiskaly_api_key_protected = table.Column<string>(type: "text", nullable: true),
                    fiskaly_api_secret_protected = table.Column<string>(type: "text", nullable: true),
                    fiskaly_credential_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    fiskaly_environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    fiskaly_tss_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: true),
                    simulated_algorithm = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    simulated_failure_rate_percent = table.Column<int>(type: "integer", nullable: true),
                    simulated_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    simulated_outage = table.Column<bool>(type: "boolean", nullable: true),
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
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signature_counter = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transaction_number = table.Column<long>(type: "bigint", nullable: true)
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protected_private_key = table.Column<string>(type: "text", nullable: false),
                    public_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    signature_algorithm = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    signature_counter = table.Column<long>(type: "bigint", nullable: false),
                    time_format = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    transaction_counter = table.Column<long>(type: "bigint", nullable: false)
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
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_data = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    process_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    signature_counter = table.Column<long>(type: "bigint", nullable: true),
                    start_signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    start_signature_counter = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transaction_key = table.Column<Guid>(type: "uuid", nullable: false),
                    tse_id = table.Column<Guid>(type: "uuid", nullable: false)
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
        }
    }
}
