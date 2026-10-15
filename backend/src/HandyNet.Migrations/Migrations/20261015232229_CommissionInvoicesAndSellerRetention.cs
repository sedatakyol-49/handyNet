using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class CommissionInvoicesAndSellerRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "invoicing");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymized_at",
                schema: "ankauf",
                table: "intakes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    number_year = table.Column<int>(type: "integer", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    billing_month = table.Column<DateOnly>(type: "date", nullable: false),
                    service_period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    service_period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    referenced_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    referenced_invoice_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    referenced_invoice_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    vat_rate_basis_points = table.Column<int>(type: "integer", nullable: false),
                    total_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    issuer = table.Column<string>(type: "jsonb", nullable: false),
                    recipient = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.UniqueConstraint("ak_invoices_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_invoices_kind", "(kind = 'Invoice' AND total_net_minor > 0 AND total_vat_minor >= 0 AND referenced_invoice_id IS NULL) OR (kind = 'CreditNote' AND total_net_minor < 0 AND total_vat_minor <= 0 AND referenced_invoice_id IS NOT NULL AND referenced_invoice_number IS NOT NULL)");
                    table.CheckConstraint("ck_invoices_number", "sequence_number BETWEEN 1 AND 999999 AND number_year = extract(year FROM issue_date) AND number = (CASE kind WHEN 'Invoice' THEN 'HN-' ELSE 'HN-RK-' END) || lpad(number_year::text, 4, '0') || '-' || lpad(sequence_number::text, 6, '0')");
                    table.CheckConstraint("ck_invoices_period", "service_period_end >= service_period_start AND extract(day FROM billing_month) = 1");
                    table.CheckConstraint("ck_invoices_totals", "total_net_minor + total_vat_minor = total_gross_minor");
                    table.ForeignKey(
                        name: "fk_invoices_invoices_organization_id_referenced_invoice_id",
                        columns: x => new { x.organization_id, x.referenced_invoice_id },
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "number_sequences",
                schema: "invoicing",
                columns: table => new
                {
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_number_sequences", x => new { x.kind, x.year });
                    table.CheckConstraint("ck_number_sequences_next", "next_number >= 1");
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "invoicing",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    net_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    credited_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credited_line_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => new { x.invoice_id, x.line_number });
                    table.CheckConstraint("ck_invoice_lines_amounts", "gross_minor = net_minor + vat_minor");
                    table.CheckConstraint("ck_invoice_lines_kind", "(kind = 'Invoice' AND net_minor > 0 AND vat_minor >= 0 AND credited_invoice_id IS NULL AND credited_line_number IS NULL) OR (kind = 'CreditNote' AND net_minor < 0 AND vat_minor <= 0 AND credited_invoice_id IS NOT NULL AND credited_line_number IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoice_lines_credited_invoice_id_credited_li",
                        columns: x => new { x.credited_invoice_id, x.credited_line_number },
                        principalSchema: "invoicing",
                        principalTable: "invoice_lines",
                        principalColumns: new[] { "invoice_id", "line_number" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_organization_id_invoice_id",
                        columns: x => new { x.organization_id, x.invoice_id },
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_intakes_anonymized",
                schema: "ankauf",
                table: "intakes",
                sql: "anonymized_at IS NULL OR seller = '{\"firstName\":\"anonymisiert\",\"lastName\":\"anonymisiert\",\"street\":\"anonymisiert\",\"postalCode\":\"00000\",\"city\":\"anonymisiert\",\"birthDate\":\"1900-01-01\",\"idDocumentType\":0,\"idDocumentNumber\":\"ANONYMISIERT\"}'::jsonb");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_credited_invoice_id_credited_line_number",
                schema: "invoicing",
                table: "invoice_lines",
                columns: new[] { "credited_invoice_id", "credited_line_number" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_organization_id",
                schema: "invoicing",
                table: "invoice_lines",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_organization_id_invoice_id",
                schema: "invoicing",
                table: "invoice_lines",
                columns: new[] { "organization_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_payment_id_kind",
                schema: "invoicing",
                table: "invoice_lines",
                columns: new[] { "payment_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_billing_month_kind",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "billing_month", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_kind_number_year_sequence_number",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "kind", "number_year", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_number",
                schema: "invoicing",
                table: "invoices",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_organization_id_referenced_invoice_id",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "organization_id", "referenced_invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_one_credit_note_per_invoice_month",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "referenced_invoice_id", "billing_month" },
                unique: true,
                filter: "kind = 'CreditNote'");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_one_per_seller_month",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "organization_id", "billing_month" },
                unique: true,
                filter: "kind = 'Invoice'");

            // ---- Grants: documents are insert-only; the counter is only advanced (never deleted) ---------------------------
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA invoicing TO handynet_app;
                GRANT SELECT, INSERT ON invoicing.invoices, invoicing.invoice_lines TO handynet_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON invoicing.invoices, invoicing.invoice_lines FROM handynet_app;
                GRANT SELECT, INSERT, UPDATE ON invoicing.number_sequences TO handynet_app;
                REVOKE DELETE, TRUNCATE ON invoicing.number_sequences FROM handynet_app;
                """);

            // ---- Row-Level Security: a seller sees only its own invoices (recipient = organization_id) --------------------------
            foreach (var table in new[] { "invoices", "invoice_lines" })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE invoicing.{table} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON invoicing.{table}
                        USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                    """);
            }

            // ---- Immutability even for the owner role (GoBD): corrections are new credit notes --------------------------------
            migrationBuilder.Sql("""
                CREATE FUNCTION invoicing.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'invoicing % rows are immutable (%)', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END $$;
                CREATE TRIGGER invoices_immutable BEFORE UPDATE OR DELETE ON invoicing.invoices
                    FOR EACH ROW EXECUTE FUNCTION invoicing.forbid_change();
                CREATE TRIGGER invoice_lines_immutable BEFORE UPDATE OR DELETE ON invoicing.invoice_lines
                    FOR EACH ROW EXECUTE FUNCTION invoicing.forbid_change();
                CREATE TRIGGER number_sequences_no_delete BEFORE DELETE ON invoicing.number_sequences
                    FOR EACH ROW EXECUTE FUNCTION invoicing.forbid_change();
                CREATE TRIGGER invoices_no_truncate BEFORE TRUNCATE ON invoicing.invoices
                    FOR EACH STATEMENT EXECUTE FUNCTION invoicing.forbid_change();
                CREATE TRIGGER invoice_lines_no_truncate BEFORE TRUNCATE ON invoicing.invoice_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION invoicing.forbid_change();
                CREATE TRIGGER number_sequences_no_truncate BEFORE TRUNCATE ON invoicing.number_sequences
                    FOR EACH STATEMENT EXECUTE FUNCTION invoicing.forbid_change();
                """);

            // ---- Gapless numbers per series (Invoice / CreditNote) and year, enforced by the database ----------------------------
            // 1. A counter starts at 1 and only ever moves by +1.
            // 2. At commit, every number taken from a counter must have its document (deferred constraint trigger).
            // 3. A document must carry exactly the number just allocated (the counter row stays locked until commit).
            // 4. At commit, a document's totals equal the sum of its lines, and it has at least one line.
            // 5. Lines belong to a document of the same kind and organization; a credit line mirrors the invoice line it reverses.
            migrationBuilder.Sql("""
                CREATE FUNCTION invoicing.guard_sequence() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW.next_number <> 2 THEN
                        RAISE EXCEPTION 'invoice numbers start at 1' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'UPDATE' AND (NEW.next_number <> OLD.next_number + 1 OR NEW.kind <> OLD.kind OR NEW.year <> OLD.year) THEN
                        RAISE EXCEPTION 'invoice numbers advance by one (% -> %)', OLD.next_number, NEW.next_number USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER number_sequences_guard BEFORE INSERT OR UPDATE ON invoicing.number_sequences
                    FOR EACH ROW EXECUTE FUNCTION invoicing.guard_sequence();

                CREATE FUNCTION invoicing.check_number_used() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM invoicing.invoices i
                                   WHERE i.kind = NEW.kind AND i.number_year = NEW.year AND i.sequence_number = NEW.next_number - 1) THEN
                        RAISE EXCEPTION '% number % of % was allocated but not used (gap)', NEW.kind, NEW.next_number - 1, NEW.year
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER number_sequences_used AFTER INSERT OR UPDATE ON invoicing.number_sequences
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION invoicing.check_number_used();

                CREATE FUNCTION invoicing.check_invoice() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM invoicing.number_sequences s
                                   WHERE s.kind = NEW.kind AND s.year = NEW.number_year AND s.next_number = NEW.sequence_number + 1) THEN
                        RAISE EXCEPTION 'number % was not just allocated' , NEW.number USING ERRCODE = '23514';
                    END IF;
                    IF NEW.kind = 'CreditNote' AND NOT EXISTS (
                        SELECT 1 FROM invoicing.invoices o
                        WHERE o.id = NEW.referenced_invoice_id AND o.organization_id = NEW.organization_id AND o.kind = 'Invoice'
                          AND o.number = NEW.referenced_invoice_number AND o.issue_date IS NOT DISTINCT FROM NEW.referenced_invoice_date
                          AND o.vat_rate_basis_points = NEW.vat_rate_basis_points
                          AND o.service_period_start = NEW.service_period_start AND o.service_period_end = NEW.service_period_end) THEN
                        RAISE EXCEPTION 'a credit note must reference an invoice of the same seller' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER invoices_check BEFORE INSERT ON invoicing.invoices
                    FOR EACH ROW EXECUTE FUNCTION invoicing.check_invoice();

                CREATE FUNCTION invoicing.check_totals() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                DECLARE
                    v_count bigint;
                    v_net bigint;
                    v_vat bigint;
                BEGIN
                    SELECT count(*), coalesce(sum(net_minor), 0), coalesce(sum(vat_minor), 0) INTO v_count, v_net, v_vat
                    FROM invoicing.invoice_lines WHERE invoice_id = NEW.id;
                    IF v_count = 0 OR v_net <> NEW.total_net_minor OR v_vat <> NEW.total_vat_minor THEN
                        RAISE EXCEPTION 'totals of % do not match its lines', NEW.number USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER invoices_totals AFTER INSERT ON invoicing.invoices
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION invoicing.check_totals();

                CREATE FUNCTION invoicing.check_line() RETURNS trigger
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                DECLARE
                    v_doc invoicing.invoices%ROWTYPE;
                    v_orig invoicing.invoice_lines%ROWTYPE;
                BEGIN
                    SELECT * INTO v_doc FROM invoicing.invoices WHERE id = NEW.invoice_id;
                    IF NOT FOUND OR v_doc.kind <> NEW.kind OR v_doc.organization_id <> NEW.organization_id THEN
                        RAISE EXCEPTION 'line does not match its document' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.kind = 'CreditNote' THEN
                        SELECT * INTO v_orig FROM invoicing.invoice_lines
                        WHERE invoice_id = NEW.credited_invoice_id AND line_number = NEW.credited_line_number;
                        IF NOT FOUND OR v_orig.invoice_id <> v_doc.referenced_invoice_id OR v_orig.payment_id <> NEW.payment_id
                           OR v_orig.order_id <> NEW.order_id OR v_orig.net_minor <> -NEW.net_minor OR v_orig.vat_minor <> -NEW.vat_minor THEN
                            RAISE EXCEPTION 'a credit line must exactly reverse a line of the corrected invoice' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER invoice_lines_check BEFORE INSERT ON invoicing.invoice_lines
                    FOR EACH ROW EXECUTE FUNCTION invoicing.check_line();
                """);

            // ---- Cross-tenant, id-only entry points for jobs and the platform admin (ADR-0005) ---------------------------------
            migrationBuilder.Sql("""
                CREATE FUNCTION payments.commission_sellers(p_from timestamptz, p_until timestamptz)
                    RETURNS TABLE (seller_organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT DISTINCT p.seller_organization_id FROM payments.payments p
                    WHERE (p.commission_net_minor > 0 OR p.commission_vat_minor > 0)
                      AND ((p.paid_at >= p_from AND p.paid_at < p_until) OR (p.refunded_at >= p_from AND p.refunded_at < p_until))
                $$;

                CREATE FUNCTION invoicing.documents_for_month(p_month date)
                    RETURNS TABLE (id uuid, organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT i.id, i.organization_id FROM invoicing.invoices i WHERE i.billing_month = p_month
                $$;

                CREATE FUNCTION ankauf.intakes_due_for_anonymization(p_cutoff timestamptz, p_limit integer)
                    RETURNS TABLE (id uuid, organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT i.id, i.organization_id FROM ankauf.intakes i
                    WHERE i.anonymized_at IS NULL AND coalesce(i.completed_at, i.received_at) < p_cutoff
                    ORDER BY coalesce(i.completed_at, i.received_at)
                    LIMIT p_limit
                $$;

                REVOKE ALL ON FUNCTION payments.commission_sellers(timestamptz, timestamptz) FROM PUBLIC;
                REVOKE ALL ON FUNCTION invoicing.documents_for_month(date) FROM PUBLIC;
                REVOKE ALL ON FUNCTION ankauf.intakes_due_for_anonymization(timestamptz, integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION payments.commission_sellers(timestamptz, timestamptz) TO handynet_app;
                GRANT EXECUTE ON FUNCTION invoicing.documents_for_month(date) TO handynet_app;
                GRANT EXECUTE ON FUNCTION ankauf.intakes_due_for_anonymization(timestamptz, integer) TO handynet_app;
                """);

            // ---- Ankauf intakes: UPDATE stays allowed, but only for status transitions and the one-way anonymization ---------------
            migrationBuilder.Sql("""
                CREATE FUNCTION ankauf.guard_intake() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'intakes are never deleted' USING ERRCODE = '42501';
                    END IF;
                    IF (NEW.id, NEW.organization_id, NEW.quote_id, NEW.imei, NEW.received_at, NEW.received_by)
                       IS DISTINCT FROM (OLD.id, OLD.organization_id, OLD.quote_id, OLD.imei, OLD.received_at, OLD.received_by) THEN
                        RAISE EXCEPTION 'intake identity columns are immutable' USING ERRCODE = '42501';
                    END IF;
                    IF OLD.anonymized_at IS NOT NULL AND (NEW.anonymized_at IS DISTINCT FROM OLD.anonymized_at OR NEW.seller IS DISTINCT FROM OLD.seller) THEN
                        RAISE EXCEPTION 'anonymized seller data cannot be changed' USING ERRCODE = '42501';
                    END IF;
                    IF NEW.seller IS DISTINCT FROM OLD.seller AND NEW.anonymized_at IS NULL THEN
                        RAISE EXCEPTION 'seller data can only be changed by anonymization' USING ERRCODE = '42501';
                    END IF;
                    IF (OLD.status IN ('Completed', 'Rejected') OR (OLD.anonymized_at IS NULL AND NEW.anonymized_at IS NOT NULL))
                       AND (NEW.status, NEW.inspected_condition, NEW.inspected_defects, NEW.inspected_battery_health_percent, NEW.inspection_notes,
                            NEW.inspected_offer_minor, NEW.inspected_max_consumer_price_minor, NEW.inspected_at, NEW.paid_minor, NEW.payment_method,
                            NEW.stock_unit_id, NEW.completed_at, NEW.rejection_reason)
                       IS DISTINCT FROM (OLD.status, OLD.inspected_condition, OLD.inspected_defects, OLD.inspected_battery_health_percent, OLD.inspection_notes,
                            OLD.inspected_offer_minor, OLD.inspected_max_consumer_price_minor, OLD.inspected_at, OLD.paid_minor, OLD.payment_method,
                            OLD.stock_unit_id, OLD.completed_at, OLD.rejection_reason) THEN
                        RAISE EXCEPTION 'closed intakes change only by anonymization, and anonymization changes nothing else' USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER intakes_guard BEFORE UPDATE OR DELETE ON ankauf.intakes
                    FOR EACH ROW EXECUTE FUNCTION ankauf.guard_intake();
                """);

            // ---- Personal data no longer goes to the audit log; remove what earlier versions copied there (GDPR) -------------------
            migrationBuilder.Sql("""
                UPDATE infra.audit_log SET changes = jsonb_set(changes, '{Seller}', to_jsonb('[personal data]'::text))
                WHERE entity_type = 'Intake' AND changes ? 'Seller';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The audit-log redaction is not reversed (the removed personal data is gone on purpose).
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS intakes_guard ON ankauf.intakes;
                DROP FUNCTION IF EXISTS ankauf.guard_intake();
                DROP FUNCTION IF EXISTS ankauf.intakes_due_for_anonymization(timestamptz, integer);
                DROP FUNCTION IF EXISTS invoicing.documents_for_month(date);
                DROP FUNCTION IF EXISTS payments.commission_sellers(timestamptz, timestamptz);
                DROP TRIGGER IF EXISTS invoices_no_truncate ON invoicing.invoices;
                DROP TRIGGER IF EXISTS invoice_lines_no_truncate ON invoicing.invoice_lines;
                DROP TRIGGER IF EXISTS number_sequences_no_truncate ON invoicing.number_sequences;
                """);

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "number_sequences",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "invoicing");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS invoicing.check_line();
                DROP FUNCTION IF EXISTS invoicing.check_totals();
                DROP FUNCTION IF EXISTS invoicing.check_invoice();
                DROP FUNCTION IF EXISTS invoicing.check_number_used();
                DROP FUNCTION IF EXISTS invoicing.guard_sequence();
                DROP FUNCTION IF EXISTS invoicing.forbid_change();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_intakes_anonymized",
                schema: "ankauf",
                table: "intakes");

            migrationBuilder.DropColumn(
                name: "anonymized_at",
                schema: "ankauf",
                table: "intakes");
        }
    }
}
