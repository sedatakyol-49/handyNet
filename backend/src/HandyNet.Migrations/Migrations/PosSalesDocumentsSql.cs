using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosSalesDocumentsAndZNumber</c> (ADR-0014): gapless Z numbers per register, quotations, sales
/// invoices sharing the organization's single gapless invoice series with invoices for POS sales, credit notes, append-only
/// invoice payments (cash only through a till receipt), document emails — grants, Row-Level Security and triggers.
/// </summary>
internal static class PosSalesDocumentsSql
{
    /// <summary>Z numbers for sessions closed before this migration: 1, 2, … per register in closing order.</summary>
    public static void Backfill(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.cash_sessions DISABLE TRIGGER cash_sessions_final;
            UPDATE pos.cash_sessions s SET z_number = n.z
            FROM (SELECT id, row_number() OVER (PARTITION BY register_id ORDER BY closed_at, id) AS z
                  FROM pos.cash_sessions WHERE status = 'Closed') n
            WHERE s.id = n.id;
            ALTER TABLE pos.cash_sessions ENABLE TRIGGER cash_sessions_final;
            UPDATE pos.registers r
            SET next_z_number = 1 + COALESCE((SELECT max(s.z_number) FROM pos.cash_sessions s WHERE s.register_id = r.id), 0);
            """);

    public static void Up(MigrationBuilder m)
    {
        // ---- Z numbers: gapless per register, DB-enforced like receipt numbers --------------------------------------------
        // 1. The counter only moves by +1 (guard_register, extended).
        // 2. At commit, a number taken from the counter must belong to a closed session of that register (deferred).
        // 3. A session is closed with exactly the number just allocated; sessions are created open.
        m.Sql("""
            CREATE OR REPLACE FUNCTION pos.guard_register() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.next_receipt_number NOT IN (OLD.next_receipt_number, OLD.next_receipt_number + 1) THEN
                    RAISE EXCEPTION 'receipt numbers advance by one (% -> %)', OLD.next_receipt_number, NEW.next_receipt_number
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.next_z_number NOT IN (OLD.next_z_number, OLD.next_z_number + 1) THEN
                    RAISE EXCEPTION 'Z numbers advance by one (% -> %)', OLD.next_z_number, NEW.next_z_number USING ERRCODE = '23514';
                END IF;
                IF NEW.organization_id <> OLD.organization_id OR NEW.warehouse_id <> OLD.warehouse_id THEN
                    RAISE EXCEPTION 'a register cannot move to another organization or warehouse' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;

            CREATE FUNCTION pos.check_z_number_used() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.next_z_number > OLD.next_z_number AND NOT EXISTS (
                    SELECT 1 FROM pos.cash_sessions s
                    WHERE s.register_id = NEW.id AND s.status = 'Closed' AND s.z_number = NEW.next_z_number - 1) THEN
                    RAISE EXCEPTION 'Z number % of register % was allocated but not used (gap)', NEW.next_z_number - 1, NEW.id
                        USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END $$;
            CREATE CONSTRAINT TRIGGER registers_z_number_used AFTER UPDATE OF next_z_number ON pos.registers
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_z_number_used();

            CREATE FUNCTION pos.check_session_z_number() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'INSERT' AND NEW.status <> 'Open' THEN
                    RAISE EXCEPTION 'cash sessions are opened, then closed' USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'UPDATE' AND OLD.status = 'Open' AND NEW.status = 'Closed' AND NOT EXISTS (
                    SELECT 1 FROM pos.registers g WHERE g.id = NEW.register_id AND g.next_z_number = NEW.z_number + 1) THEN
                    RAISE EXCEPTION 'Z number % was not just allocated from register %', NEW.z_number, NEW.register_id USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER cash_sessions_z_number BEFORE INSERT OR UPDATE ON pos.cash_sessions
                FOR EACH ROW EXECUTE FUNCTION pos.check_session_z_number();
            """);

        // ---- One invoice series per organization: invoices for POS sales and sales invoices/credit notes share it ----------
        m.Sql("""
            CREATE OR REPLACE FUNCTION pos.check_invoice_number_used() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pos.invoices i
                               WHERE i.organization_id = NEW.organization_id AND i.year = NEW.year AND i.sequence_number = NEW.next_number - 1)
                   AND NOT EXISTS (SELECT 1 FROM pos.sales_invoices s
                               WHERE s.organization_id = NEW.organization_id AND s.year = NEW.year AND s.sequence_number = NEW.next_number - 1
                                 AND s.status = 'Issued') THEN
                    RAISE EXCEPTION 'invoice number % of % was allocated but not used (gap)', NEW.next_number - 1, NEW.year USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END $$;

            CREATE OR REPLACE FUNCTION pos.check_invoice() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pos.invoice_sequences s
                               WHERE s.organization_id = NEW.organization_id AND s.year = NEW.year AND s.next_number = NEW.sequence_number + 1) THEN
                    RAISE EXCEPTION 'invoice number % was not just allocated', NEW.number USING ERRCODE = '23514';
                END IF;
                IF EXISTS (SELECT 1 FROM pos.sales_invoices s
                           WHERE s.organization_id = NEW.organization_id AND s.year = NEW.year AND s.sequence_number = NEW.sequence_number) THEN
                    RAISE EXCEPTION 'invoice number % is already used by a sales invoice', NEW.number USING ERRCODE = '23505';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pos.receipts r
                               WHERE r.id = NEW.receipt_id AND r.organization_id = NEW.organization_id AND r.kind = 'Sale'
                                 AND r.total_gross_minor = NEW.total_gross_minor) THEN
                    RAISE EXCEPTION 'an invoice must match a sale receipt of the organization' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            """);

        // ---- Tenant isolation and grants ------------------------------------------------------------------------------------
        foreach (var table in new[] { "quotations", "quotation_sequences", "sales_invoices", "sales_invoice_payments", "sales_document_emails" })
            m.Sql($"""
                ALTER TABLE pos.{table} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON pos.{table}
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                """);
        m.Sql("""
            GRANT SELECT, INSERT, UPDATE ON pos.quotations, pos.quotation_sequences, pos.sales_invoices TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.quotations, pos.quotation_sequences, pos.sales_invoices FROM handynet_app;
            GRANT SELECT, INSERT ON pos.sales_invoice_payments, pos.sales_document_emails TO handynet_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON pos.sales_invoice_payments, pos.sales_document_emails FROM handynet_app;

            CREATE TRIGGER quotations_no_delete BEFORE DELETE ON pos.quotations FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER quotations_no_truncate BEFORE TRUNCATE ON pos.quotations FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER quotation_sequences_no_delete BEFORE DELETE ON pos.quotation_sequences FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER sales_invoices_no_truncate BEFORE TRUNCATE ON pos.sales_invoices FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER sales_invoice_payments_immutable BEFORE UPDATE OR DELETE ON pos.sales_invoice_payments
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER sales_invoice_payments_no_truncate BEFORE TRUNCATE ON pos.sales_invoice_payments
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER sales_document_emails_immutable BEFORE UPDATE OR DELETE ON pos.sales_document_emails
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER sales_document_emails_no_truncate BEFORE TRUNCATE ON pos.sales_document_emails
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

            -- Quotation numbers: start at 1 and move by +1 (sequential; not a legal requirement, kept anyway).
            CREATE FUNCTION pos.guard_quotation_sequence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'INSERT' AND NEW.next_number <> 2 THEN
                    RAISE EXCEPTION 'quotation numbers start at 1' USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'UPDATE' AND (NEW.next_number <> OLD.next_number + 1 OR NEW.organization_id <> OLD.organization_id OR NEW.year <> OLD.year) THEN
                    RAISE EXCEPTION 'quotation numbers advance by one' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER quotation_sequences_guard BEFORE INSERT OR UPDATE ON pos.quotation_sequences
                FOR EACH ROW EXECUTE FUNCTION pos.guard_quotation_sequence();
            """);

        // ---- Sales invoices: drafts editable; issued rows (invoice or credit note) final, also for the owner role ------------
        // An issued row carries exactly the number just taken from the shared series, never one used by an invoice for a POS
        // sale; a credit note mirrors an issued invoice of the organization (its row is locked, so payments queue behind it).
        m.Sql("""
            CREATE FUNCTION pos.guard_sales_invoice() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'sales invoices are never deleted' USING ERRCODE = '42501';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    IF OLD.status <> 'Draft' THEN
                        RAISE EXCEPTION 'sales invoice % is final (%)', OLD.id, OLD.status USING ERRCODE = '42501';
                    END IF;
                    IF NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.kind <> OLD.kind
                       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
                        RAISE EXCEPTION 'sales invoice % cannot be changed this way', OLD.id USING ERRCODE = '42501';
                    END IF;
                ELSIF NEW.status = 'Discarded' THEN
                    RAISE EXCEPTION 'a sales invoice is created as draft or issued' USING ERRCODE = '23514';
                END IF;
                IF NEW.status = 'Issued' THEN
                    IF NOT EXISTS (SELECT 1 FROM pos.invoice_sequences s
                                   WHERE s.organization_id = NEW.organization_id AND s.year = NEW.year AND s.next_number = NEW.sequence_number + 1) THEN
                        RAISE EXCEPTION 'invoice number % was not just allocated', NEW.number USING ERRCODE = '23514';
                    END IF;
                    IF EXISTS (SELECT 1 FROM pos.invoices i
                               WHERE i.organization_id = NEW.organization_id AND i.year = NEW.year AND i.sequence_number = NEW.sequence_number) THEN
                        RAISE EXCEPTION 'invoice number % is already used by an invoice for a POS sale', NEW.number USING ERRCODE = '23505';
                    END IF;
                    IF NEW.kind = 'CreditNote' THEN
                        PERFORM 1 FROM pos.sales_invoices o WHERE o.id = NEW.original_invoice_id FOR UPDATE;
                        IF NOT EXISTS (SELECT 1 FROM pos.sales_invoices o
                                       WHERE o.id = NEW.original_invoice_id AND o.organization_id = NEW.organization_id AND o.kind = 'Invoice'
                                         AND o.status = 'Issued' AND o.total_gross_minor = -NEW.total_gross_minor) THEN
                            RAISE EXCEPTION 'a credit note mirrors an issued invoice of the organization' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER sales_invoices_guard BEFORE INSERT OR UPDATE OR DELETE ON pos.sales_invoices
                FOR EACH ROW EXECUTE FUNCTION pos.guard_sales_invoice();
            """);

        // ---- Payments: append-only; never beyond the invoice total, never on a cancelled invoice; a till payment matches its
        // receipt, and every invoice payment receipt has its payment (deferred) --------------------------------------------------
        m.Sql("""
            CREATE FUNCTION pos.check_sales_invoice_payment() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            DECLARE
                v_total bigint;
                v_paid bigint;
            BEGIN
                SELECT total_gross_minor INTO v_total FROM pos.sales_invoices
                WHERE id = NEW.invoice_id AND organization_id = NEW.organization_id AND kind = 'Invoice' AND status = 'Issued'
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'payments are recorded on issued invoices of the organization' USING ERRCODE = '23514';
                END IF;
                IF EXISTS (SELECT 1 FROM pos.sales_invoices c WHERE c.original_invoice_id = NEW.invoice_id) THEN
                    RAISE EXCEPTION 'invoice % was cancelled with a credit note', NEW.invoice_id USING ERRCODE = '23514';
                END IF;
                SELECT COALESCE(sum(amount_minor), 0) INTO v_paid FROM pos.sales_invoice_payments WHERE invoice_id = NEW.invoice_id;
                IF v_paid + NEW.amount_minor > v_total THEN
                    RAISE EXCEPTION 'payments (%) would exceed the invoice total (%)', v_paid + NEW.amount_minor, v_total USING ERRCODE = '23514';
                END IF;
                IF NEW.receipt_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM pos.receipts r
                    WHERE r.id = NEW.receipt_id AND r.organization_id = NEW.organization_id AND r.kind = 'InvoicePayment'
                      AND r.sales_invoice_id = NEW.invoice_id AND r.total_gross_minor = NEW.amount_minor) THEN
                    RAISE EXCEPTION 'a till payment must match its invoice payment receipt' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER sales_invoice_payments_check BEFORE INSERT ON pos.sales_invoice_payments
                FOR EACH ROW EXECUTE FUNCTION pos.check_sales_invoice_payment();

            CREATE FUNCTION pos.check_invoice_payment_receipt() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pos.sales_invoice_payments p WHERE p.receipt_id = NEW.id AND p.invoice_id = NEW.sales_invoice_id) THEN
                    RAISE EXCEPTION 'invoice payment receipt % has no invoice payment', NEW.id USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END $$;
            CREATE CONSTRAINT TRIGGER receipts_invoice_payment_recorded AFTER INSERT ON pos.receipts
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.kind = 'InvoicePayment')
                EXECUTE FUNCTION pos.check_invoice_payment_receipt();
            """);
    }

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP TRIGGER IF EXISTS receipts_invoice_payment_recorded ON pos.receipts;
            DROP FUNCTION IF EXISTS pos.check_invoice_payment_receipt();
            DROP TRIGGER IF EXISTS sales_invoice_payments_check ON pos.sales_invoice_payments;
            DROP FUNCTION IF EXISTS pos.check_sales_invoice_payment();
            DROP TRIGGER IF EXISTS sales_invoices_guard ON pos.sales_invoices;
            DROP FUNCTION IF EXISTS pos.guard_sales_invoice();
            DROP TRIGGER IF EXISTS quotation_sequences_guard ON pos.quotation_sequences;
            DROP FUNCTION IF EXISTS pos.guard_quotation_sequence();
            DROP TRIGGER IF EXISTS registers_z_number_used ON pos.registers;
            DROP FUNCTION IF EXISTS pos.check_z_number_used();
            DROP TRIGGER IF EXISTS cash_sessions_z_number ON pos.cash_sessions;
            DROP FUNCTION IF EXISTS pos.check_session_z_number();

            CREATE OR REPLACE FUNCTION pos.guard_register() RETURNS trigger LANGUAGE plpgsql AS $$
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

            CREATE OR REPLACE FUNCTION pos.check_invoice_number_used() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pos.invoices i
                               WHERE i.organization_id = NEW.organization_id AND i.year = NEW.year AND i.sequence_number = NEW.next_number - 1) THEN
                    RAISE EXCEPTION 'invoice number % of % was allocated but not used (gap)', NEW.next_number - 1, NEW.year USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END $$;

            CREATE OR REPLACE FUNCTION pos.check_invoice() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pos.invoice_sequences s
                               WHERE s.organization_id = NEW.organization_id AND s.year = NEW.year AND s.next_number = NEW.sequence_number + 1) THEN
                    RAISE EXCEPTION 'invoice number % was not just allocated', NEW.number USING ERRCODE = '23514';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pos.receipts r
                               WHERE r.id = NEW.receipt_id AND r.organization_id = NEW.organization_id AND r.kind = 'Sale'
                                 AND r.total_gross_minor = NEW.total_gross_minor) THEN
                    RAISE EXCEPTION 'an invoice must match a sale receipt of the organization' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            """);
}
