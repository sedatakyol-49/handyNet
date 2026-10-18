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
                name: "invoice_sequences",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_sequences", x => new { x.organization_id, x.year });
                    table.CheckConstraint("ck_invoice_sequences_next", "next_number >= 2");
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: false),
                    customer = table.Column<string>(type: "jsonb", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    total_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_gross_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices1", x => x.id);
                    table.CheckConstraint("ck_invoices_number", "sequence_number >= 1 AND year BETWEEN 2000 AND 9999 AND number = 'RE-' || year::text || '-' || lpad(sequence_number::text, 6, '0')");
                    table.CheckConstraint("ck_invoices_totals", "total_gross_minor > 0 AND total_vat_minor >= 0 AND total_net_minor + total_vat_minor = total_gross_minor");
                    table.ForeignKey(
                        name: "fk_invoices_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
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
                name: "ix_invoices_organization_id_number",
                schema: "pos",
                table: "invoices",
                columns: new[] { "organization_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_organization_id_receipt_id",
                schema: "pos",
                table: "invoices",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_organization_id_year_sequence_number",
                schema: "pos",
                table: "invoices",
                columns: new[] { "organization_id", "year", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_receipt_id",
                schema: "pos",
                table: "invoices",
                column: "receipt_id",
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
                name: "invoice_sequences",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "invoices",
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
        }
    }
}
