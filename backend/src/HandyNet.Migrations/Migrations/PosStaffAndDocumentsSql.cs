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
            """);
    }
}
