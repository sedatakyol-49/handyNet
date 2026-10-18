using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosStaffAndDocuments</c> (grants, Row-Level Security, triggers, functions), kept apart from
/// the generated code so the migration stays readable.
/// </summary>
internal static class PosStaffAndDocumentsSql
{
    private static readonly string[] TenantTables = ["cashier_pins", "cashier_sessions", "approvals"];

    public static void Up(MigrationBuilder m)
    {
        // ---- Grants: PINs and cashier sessions are updated (attempts, lockout, last seen, end) but never deleted ------------
        m.Sql("""
            GRANT SELECT, INSERT, UPDATE ON pos.cashier_pins, pos.cashier_sessions, pos.approvals TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.cashier_pins, pos.cashier_sessions, pos.approvals FROM handynet_app;
            """);

        foreach (var table in TenantTables)
            m.Sql($"""
                ALTER TABLE pos.{table} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON pos.{table}
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                """);

        // ---- Cashier sessions: identity is fixed; only last-seen and the (one-way) end may change; nothing is deleted -------
        m.Sql("""
            CREATE FUNCTION pos.guard_cashier_session() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'cashier sessions are never deleted' USING ERRCODE = '42501';
                END IF;
                IF (NEW.id, NEW.organization_id, NEW.register_id, NEW.cashier_user_id, NEW.device_user_id, NEW.token_hash, NEW.created_at, NEW.expires_at)
                   IS DISTINCT FROM (OLD.id, OLD.organization_id, OLD.register_id, OLD.cashier_user_id, OLD.device_user_id, OLD.token_hash, OLD.created_at, OLD.expires_at)
                   OR (OLD.ended_at IS NOT NULL AND (NEW.ended_at, NEW.ended_reason, NEW.last_seen_at) IS DISTINCT FROM (OLD.ended_at, OLD.ended_reason, OLD.last_seen_at)) THEN
                    RAISE EXCEPTION 'cashier session % cannot be changed this way', OLD.id USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER cashier_sessions_guard BEFORE UPDATE OR DELETE ON pos.cashier_sessions
                FOR EACH ROW EXECUTE FUNCTION pos.guard_cashier_session();
            CREATE TRIGGER cashier_pins_no_delete BEFORE DELETE ON pos.cashier_pins
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            """);

        // ---- Approvals: the grant is fixed; it is used once (used_at + what it was used for) and never deleted ------------
        m.Sql("""
            CREATE FUNCTION pos.guard_approval() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'approvals are never deleted' USING ERRCODE = '42501';
                END IF;
                IF OLD.used_at IS NOT NULL
                   OR (NEW.id, NEW.organization_id, NEW.register_id, NEW.permissions, NEW.approver_user_id, NEW.requested_by_user_id, NEW.device_user_id,
                       NEW.token_hash, NEW.created_at, NEW.expires_at)
                   IS DISTINCT FROM (OLD.id, OLD.organization_id, OLD.register_id, OLD.permissions, OLD.approver_user_id, OLD.requested_by_user_id,
                       OLD.device_user_id, OLD.token_hash, OLD.created_at, OLD.expires_at) THEN
                    RAISE EXCEPTION 'approval % is fixed once given or used', OLD.id USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER approvals_guard BEFORE UPDATE OR DELETE ON pos.approvals
                FOR EACH ROW EXECUTE FUNCTION pos.guard_approval();
            CREATE TRIGGER approvals_no_truncate BEFORE TRUNCATE ON pos.approvals
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            """);

        // ---- E-receipts: email requests and public links are append-only records ----------------------------------------
        foreach (var table in new[] { "receipt_emails", "receipt_links" })
            m.Sql($"""
                GRANT SELECT, INSERT ON pos.{table} TO handynet_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON pos.{table} FROM handynet_app;
                ALTER TABLE pos.{table} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON pos.{table}
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                CREATE TRIGGER {table}_immutable BEFORE UPDATE OR DELETE ON pos.{table}
                    FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
                CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON pos.{table}
                    FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
                """);

        // ---- Invoices for POS sales: immutable (GoBD), gapless numbers per organization and year (DB-enforced) ------------
        // 1. A counter starts at 1 (row inserted with next_number = 2) and only ever moves by +1.
        // 2. At commit, the number taken from a counter must have its invoice (deferred constraint trigger): no gaps.
        // 3. An invoice carries exactly the number just allocated (the counter row stays locked until commit).
        m.Sql("""
            GRANT SELECT, INSERT ON pos.invoices TO handynet_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON pos.invoices FROM handynet_app;
            GRANT SELECT, INSERT, UPDATE ON pos.invoice_sequences TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.invoice_sequences FROM handynet_app;
            ALTER TABLE pos.invoices ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON pos.invoices
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            ALTER TABLE pos.invoice_sequences ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON pos.invoice_sequences
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());

            CREATE TRIGGER invoices_immutable BEFORE UPDATE OR DELETE ON pos.invoices
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER invoices_no_truncate BEFORE TRUNCATE ON pos.invoices
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER invoice_sequences_no_delete BEFORE DELETE ON pos.invoice_sequences
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER invoice_sequences_no_truncate BEFORE TRUNCATE ON pos.invoice_sequences
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

            CREATE FUNCTION pos.guard_invoice_sequence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'INSERT' AND NEW.next_number <> 2 THEN
                    RAISE EXCEPTION 'invoice numbers start at 1' USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'UPDATE' AND (NEW.next_number <> OLD.next_number + 1 OR NEW.organization_id <> OLD.organization_id OR NEW.year <> OLD.year) THEN
                    RAISE EXCEPTION 'invoice numbers advance by one (% -> %)', OLD.next_number, NEW.next_number USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER invoice_sequences_guard BEFORE INSERT OR UPDATE ON pos.invoice_sequences
                FOR EACH ROW EXECUTE FUNCTION pos.guard_invoice_sequence();

            CREATE FUNCTION pos.check_invoice_number_used() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pos.invoices i
                               WHERE i.organization_id = NEW.organization_id AND i.year = NEW.year AND i.sequence_number = NEW.next_number - 1) THEN
                    RAISE EXCEPTION 'invoice number % of % was allocated but not used (gap)', NEW.next_number - 1, NEW.year USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END $$;
            CREATE CONSTRAINT TRIGGER invoice_sequences_used AFTER INSERT OR UPDATE ON pos.invoice_sequences
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_invoice_number_used();

            CREATE FUNCTION pos.check_invoice() RETURNS trigger
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
            CREATE TRIGGER invoices_check BEFORE INSERT ON pos.invoices
                FOR EACH ROW EXECUTE FUNCTION pos.check_invoice();
            """);

        // Public, anonymous receipt view (QR): the only cross-tenant read path, by the SHA-256 of an unguessable token. Returns
        // exactly one unexpired receipt document (the caller removes the customer block), nothing else.
        m.Sql("""
            CREATE FUNCTION pos.public_receipt(p_token_hash text)
                RETURNS TABLE (document jsonb, cancelled boolean, expires_at timestamptz)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT r.document,
                       EXISTS (SELECT 1 FROM pos.receipts s WHERE s.referenced_receipt_id = r.id AND s.kind = 'Storno'),
                       l.expires_at
                FROM pos.receipt_links l
                JOIN pos.receipts r ON r.id = l.receipt_id AND r.organization_id = l.organization_id
                WHERE l.token_hash = p_token_hash AND l.expires_at > now()
                LIMIT 1
            $$;
            REVOKE ALL ON FUNCTION pos.public_receipt(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION pos.public_receipt(text) TO handynet_app;
            """);
    }

    public static void Down(MigrationBuilder m)
    {
        m.Sql("""
            DROP TRIGGER IF EXISTS cashier_sessions_guard ON pos.cashier_sessions;
            DROP TRIGGER IF EXISTS cashier_pins_no_delete ON pos.cashier_pins;
            DROP FUNCTION IF EXISTS pos.guard_cashier_session();
            DROP TRIGGER IF EXISTS approvals_guard ON pos.approvals;
            DROP TRIGGER IF EXISTS approvals_no_truncate ON pos.approvals;
            DROP FUNCTION IF EXISTS pos.guard_approval();
            DROP FUNCTION IF EXISTS pos.public_receipt(text);
            DROP TRIGGER IF EXISTS invoices_check ON pos.invoices;
            DROP TRIGGER IF EXISTS invoice_sequences_used ON pos.invoice_sequences;
            DROP TRIGGER IF EXISTS invoice_sequences_guard ON pos.invoice_sequences;
            DROP FUNCTION IF EXISTS pos.check_invoice();
            DROP FUNCTION IF EXISTS pos.check_invoice_number_used();
            DROP FUNCTION IF EXISTS pos.guard_invoice_sequence();
            """);
    }
}
