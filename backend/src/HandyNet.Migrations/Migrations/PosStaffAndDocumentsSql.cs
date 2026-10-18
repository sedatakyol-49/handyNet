using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosStaffAndDocuments</c> (grants, Row-Level Security, triggers, functions), kept apart from
/// the generated code so the migration stays readable.
/// </summary>
internal static class PosStaffAndDocumentsSql
{
    private static readonly string[] TenantTables = ["cashier_pins", "cashier_sessions"];

    public static void Up(MigrationBuilder m)
    {
        // ---- Grants: PINs and cashier sessions are updated (attempts, lockout, last seen, end) but never deleted ------------
        m.Sql("""
            GRANT SELECT, INSERT, UPDATE ON pos.cashier_pins, pos.cashier_sessions TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.cashier_pins, pos.cashier_sessions FROM handynet_app;
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
    }

    public static void Down(MigrationBuilder m)
    {
        m.Sql("""
            DROP TRIGGER IF EXISTS cashier_sessions_guard ON pos.cashier_sessions;
            DROP TRIGGER IF EXISTS cashier_pins_no_delete ON pos.cashier_pins;
            DROP FUNCTION IF EXISTS pos.guard_cashier_session();
            """);
    }
}
