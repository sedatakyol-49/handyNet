using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PosSalesDocumentsAndZNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_receipts_kind",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_approvals_use",
                schema: "pos",
                table: "approvals");

            migrationBuilder.AddColumn<long>(
                name: "next_z_number",
                schema: "pos",
                table: "registers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "sales_invoice_id",
                schema: "pos",
                table: "receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "product_id",
                schema: "pos",
                table: "receipt_lines",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<byte[]>(
                name: "attachment_content",
                schema: "notifications",
                table: "email_messages",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_content_type",
                schema: "notifications",
                table: "email_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_name",
                schema: "notifications",
                table: "email_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "z_number",
                schema: "pos",
                table: "cash_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sales_invoice_id",
                schema: "pos",
                table: "approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_company",
                schema: "pos",
                table: "invoices",
                type: "text",
                nullable: true,
                computedColumnSql: "customer ->> 'company'",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_name",
                schema: "pos",
                table: "invoices",
                type: "text",
                nullable: true,
                computedColumnSql: "customer ->> 'name'",
                stored: true);

            migrationBuilder.CreateTable(
                name: "quotation_sequences",
                schema: "pos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotation_sequences", x => new { x.organization_id, x.year });
                    table.CheckConstraint("ck_quotation_sequences_next", "next_number >= 2");
                });

            migrationBuilder.CreateTable(
                name: "sales_document_emails",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_document_emails", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoices",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    sequence_number = table.Column<long>(type: "bigint", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer = table.Column<string>(type: "jsonb", nullable: true),
                    customer_name = table.Column<string>(type: "text", nullable: true, computedColumnSql: "customer ->> 'name'", stored: true),
                    customer_company = table.Column<string>(type: "text", nullable: true, computedColumnSql: "customer ->> 'company'", stored: true),
                    invoice_date = table.Column<DateOnly>(type: "date", nullable: true),
                    service_date = table.Column<DateOnly>(type: "date", nullable: true),
                    payment_term_days = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    price_mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reverse_charge = table.Column<bool>(type: "boolean", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    vat = table.Column<string>(type: "jsonb", nullable: false),
                    margin_details = table.Column<string>(type: "jsonb", nullable: true),
                    margin_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoices", x => x.id);
                    table.UniqueConstraint("ak_sales_invoice_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_sales_invoices_number", "(status IN ('Draft', 'Discarded') AND number IS NULL AND year IS NULL AND sequence_number IS NULL AND issued_at IS NULL AND document IS NULL) OR (status = 'Issued' AND sequence_number >= 1 AND year BETWEEN 2000 AND 9999 AND number = 'RE-' || year::text || '-' || lpad(sequence_number::text, 6, '0') AND invoice_date IS NOT NULL AND due_date IS NOT NULL AND service_date IS NOT NULL AND customer IS NOT NULL AND document IS NOT NULL AND issued_at IS NOT NULL AND issued_by IS NOT NULL)");
                    table.CheckConstraint("ck_sales_invoices_terms", "payment_term_days BETWEEN 0 AND 365 AND (due_date IS NULL OR due_date >= invoice_date)");
                    table.CheckConstraint("ck_sales_invoices_totals", "total_net_minor + total_vat_minor = total_gross_minor AND ((kind = 'Invoice' AND original_invoice_id IS NULL AND total_gross_minor >= 0 AND total_vat_minor >= 0 AND (status <> 'Issued' OR total_gross_minor > 0)) OR (kind = 'CreditNote' AND status = 'Issued' AND original_invoice_id IS NOT NULL AND total_gross_minor < 0 AND total_vat_minor <= 0 AND reason IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_sales_invoices_customers_organization_id_customer_id",
                        columns: x => new { x.organization_id, x.customer_id },
                        principalSchema: "pos",
                        principalTable: "customers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_sales_invoices_organization_id_original_invo",
                        columns: x => new { x.organization_id, x.original_invoice_id },
                        principalSchema: "pos",
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_warehouses_organization_id_warehouse_id",
                        columns: x => new { x.organization_id, x.warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotations",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer = table.Column<string>(type: "jsonb", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: true, computedColumnSql: "customer ->> 'name'", stored: true),
                    customer_company = table.Column<string>(type: "text", nullable: true, computedColumnSql: "customer ->> 'company'", stored: true),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    price_mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    vat = table.Column<string>(type: "jsonb", nullable: false),
                    margin_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    converted_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotations", x => x.id);
                    table.UniqueConstraint("ak_quotations_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_quotations_converted", "converted_invoice_id IS NULL OR status = 'Accepted'");
                    table.CheckConstraint("ck_quotations_number", "sequence_number >= 1 AND year BETWEEN 2000 AND 9999 AND number = 'AN-' || year::text || '-' || CASE WHEN sequence_number < 10000 THEN lpad(sequence_number::text, 4, '0') ELSE sequence_number::text END");
                    table.CheckConstraint("ck_quotations_totals", "total_net_minor + total_vat_minor = total_gross_minor AND total_gross_minor >= 0");
                    table.CheckConstraint("ck_quotations_valid_until", "valid_until >= issue_date");
                    table.ForeignKey(
                        name: "fk_quotations_customers_organization_id_customer_id",
                        columns: x => new { x.organization_id, x.customer_id },
                        principalSchema: "pos",
                        principalTable: "customers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quotations_sales_invoice_organization_id_converted_invoice_",
                        columns: x => new { x.organization_id, x.converted_invoice_id },
                        principalSchema: "pos",
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoice_payments",
                schema: "pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoice_payments", x => x.id);
                    table.CheckConstraint("ck_sales_invoice_payments_amount", "amount_minor > 0");
                    table.CheckConstraint("ck_sales_invoice_payments_cash", "method <> 'Cash' OR receipt_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_sales_invoice_payments_receipts_organization_id_receipt_id",
                        columns: x => new { x.organization_id, x.receipt_id },
                        principalSchema: "pos",
                        principalTable: "receipts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_payments_sales_invoices_organization_id_invoi",
                        columns: x => new { x.organization_id, x.invoice_id },
                        principalSchema: "pos",
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Backfill Z numbers of closed sessions (per register, in closing order) before the constraints apply.
            PosSalesDocumentsSql.Backfill(migrationBuilder);

            migrationBuilder.AddCheckConstraint(
                name: "ck_registers_next_z_number",
                schema: "pos",
                table: "registers",
                sql: "next_z_number >= 1");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_organization_id_sales_invoice_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "sales_invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_sales_invoice_id",
                schema: "pos",
                table: "receipts",
                column: "sales_invoice_id",
                filter: "sales_invoice_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipts_kind",
                schema: "pos",
                table: "receipts",
                sql: "(kind = 'Sale' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL AND sales_invoice_id IS NULL) OR (kind = 'InvoicePayment' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL AND sales_invoice_id IS NOT NULL) OR (kind IN ('Storno', 'Return') AND total_gross_minor <= 0 AND change_minor = 0 AND referenced_receipt_id IS NOT NULL AND sales_invoice_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipt_lines_product",
                schema: "pos",
                table: "receipt_lines",
                sql: "(product_id IS NOT NULL AND tax_treatment <> 'NotTaxable') OR (product_id IS NULL AND tax_treatment = 'NotTaxable' AND stock_unit_id IS NULL AND vat_minor = 0)");

            migrationBuilder.CreateIndex(
                name: "ux_cash_sessions_z_number",
                schema: "pos",
                table: "cash_sessions",
                columns: new[] { "register_id", "z_number" },
                unique: true,
                filter: "z_number IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cash_sessions_z_number",
                schema: "pos",
                table: "cash_sessions",
                sql: "(status = 'Open' AND z_number IS NULL) OR (status = 'Closed' AND z_number >= 1)");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_organization_id_sales_invoice_id",
                schema: "pos",
                table: "approvals",
                columns: new[] { "organization_id", "sales_invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_sales_invoice_id",
                schema: "pos",
                table: "approvals",
                column: "sales_invoice_id",
                filter: "sales_invoice_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_approvals_use",
                schema: "pos",
                table: "approvals",
                sql: "(used_at IS NULL AND used_for IS NULL AND receipt_id IS NULL AND cash_movement_id IS NULL AND session_id IS NULL AND sales_invoice_id IS NULL) OR (used_at IS NOT NULL AND used_for IS NOT NULL AND num_nonnulls(receipt_id, cash_movement_id, session_id, sales_invoice_id) = 1)");

            migrationBuilder.CreateIndex(
                name: "ix_quotations_organization_id_converted_invoice_id",
                schema: "pos",
                table: "quotations",
                columns: new[] { "organization_id", "converted_invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotations_organization_id_customer_id",
                schema: "pos",
                table: "quotations",
                columns: new[] { "organization_id", "customer_id" },
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quotations_organization_id_issue_date",
                schema: "pos",
                table: "quotations",
                columns: new[] { "organization_id", "issue_date" });

            migrationBuilder.CreateIndex(
                name: "ix_quotations_organization_id_number",
                schema: "pos",
                table: "quotations",
                columns: new[] { "organization_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotations_organization_id_year_sequence_number",
                schema: "pos",
                table: "quotations",
                columns: new[] { "organization_id", "year", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_document_emails_organization_id_document_type_documen",
                schema: "pos",
                table: "sales_document_emails",
                columns: new[] { "organization_id", "document_type", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_organization_id_invoice_id",
                schema: "pos",
                table: "sales_invoice_payments",
                columns: new[] { "organization_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_organization_id_receipt_id",
                schema: "pos",
                table: "sales_invoice_payments",
                columns: new[] { "organization_id", "receipt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_receipt_id",
                schema: "pos",
                table: "sales_invoice_payments",
                column: "receipt_id",
                unique: true,
                filter: "receipt_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_organization_id_customer_id",
                schema: "pos",
                table: "sales_invoices",
                columns: new[] { "organization_id", "customer_id" },
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_organization_id_number",
                schema: "pos",
                table: "sales_invoices",
                columns: new[] { "organization_id", "number" },
                unique: true,
                filter: "number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_organization_id_original_invoice_id",
                schema: "pos",
                table: "sales_invoices",
                columns: new[] { "organization_id", "original_invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_organization_id_status_due_date",
                schema: "pos",
                table: "sales_invoices",
                columns: new[] { "organization_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_organization_id_warehouse_id",
                schema: "pos",
                table: "sales_invoices",
                columns: new[] { "organization_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_organization_id_year_sequence_number",
                schema: "pos",
                table: "sales_invoices",
                columns: new[] { "organization_id", "year", "sequence_number" },
                unique: true,
                filter: "sequence_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_quotation_id",
                schema: "pos",
                table: "sales_invoices",
                column: "quotation_id",
                filter: "quotation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sales_invoices_one_credit_note",
                schema: "pos",
                table: "sales_invoices",
                column: "original_invoice_id",
                unique: true,
                filter: "original_invoice_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_approvals_sales_invoice_organization_id_sales_invoice_id",
                schema: "pos",
                table: "approvals",
                columns: new[] { "organization_id", "sales_invoice_id" },
                principalSchema: "pos",
                principalTable: "sales_invoices",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_receipts_sales_invoice_organization_id_sales_invoice_id",
                schema: "pos",
                table: "receipts",
                columns: new[] { "organization_id", "sales_invoice_id" },
                principalSchema: "pos",
                principalTable: "sales_invoices",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            PosSalesDocumentsSql.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            PosSalesDocumentsSql.Down(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_approvals_sales_invoice_organization_id_sales_invoice_id",
                schema: "pos",
                table: "approvals");

            migrationBuilder.DropForeignKey(
                name: "fk_receipts_sales_invoice_organization_id_sales_invoice_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropTable(
                name: "quotation_sequences",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "quotations",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "sales_document_emails",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "sales_invoice_payments",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "sales_invoices",
                schema: "pos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registers_next_z_number",
                schema: "pos",
                table: "registers");

            migrationBuilder.DropIndex(
                name: "ix_receipts_organization_id_sales_invoice_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropIndex(
                name: "ix_receipts_sales_invoice_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipts_kind",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipt_lines_product",
                schema: "pos",
                table: "receipt_lines");

            migrationBuilder.DropIndex(
                name: "ux_cash_sessions_z_number",
                schema: "pos",
                table: "cash_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cash_sessions_z_number",
                schema: "pos",
                table: "cash_sessions");

            migrationBuilder.DropIndex(
                name: "ix_approvals_organization_id_sales_invoice_id",
                schema: "pos",
                table: "approvals");

            migrationBuilder.DropIndex(
                name: "ix_approvals_sales_invoice_id",
                schema: "pos",
                table: "approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_approvals_use",
                schema: "pos",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "customer_company",
                schema: "pos",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "customer_name",
                schema: "pos",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "next_z_number",
                schema: "pos",
                table: "registers");

            migrationBuilder.DropColumn(
                name: "sales_invoice_id",
                schema: "pos",
                table: "receipts");

            migrationBuilder.DropColumn(
                name: "attachment_content",
                schema: "notifications",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "attachment_content_type",
                schema: "notifications",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "attachment_name",
                schema: "notifications",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "z_number",
                schema: "pos",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "sales_invoice_id",
                schema: "pos",
                table: "approvals");

            migrationBuilder.AlterColumn<Guid>(
                name: "product_id",
                schema: "pos",
                table: "receipt_lines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipts_kind",
                schema: "pos",
                table: "receipts",
                sql: "(kind = 'Sale' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL) OR (kind IN ('Storno', 'Return') AND total_gross_minor <= 0 AND change_minor = 0 AND referenced_receipt_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_approvals_use",
                schema: "pos",
                table: "approvals",
                sql: "(used_at IS NULL AND used_for IS NULL AND receipt_id IS NULL AND cash_movement_id IS NULL AND session_id IS NULL) OR (used_at IS NOT NULL AND used_for IS NOT NULL AND num_nonnulls(receipt_id, cash_movement_id, session_id) = 1)");
        }
    }
}
