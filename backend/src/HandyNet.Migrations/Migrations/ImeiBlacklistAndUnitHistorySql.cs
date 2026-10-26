using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>ImeiBlacklistAndUnitHistory</c> (ADR-0019): the platform-admin session flag, RLS and grants
/// of the platform-wide <c>inventory.imei_blacklist</c> (never deleted; only a one-time deactivation may be recorded), the
/// yes/no SECURITY DEFINER functions tenants use, and the <c>inventory.stock_units</c> trigger that refuses a blacklisted
/// IMEI entering stock or being sold (SQLSTATE HNB01). Idempotent, so it can be re-run safely.
/// </summary>
internal static class ImeiBlacklistAndUnitHistorySql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            -- Platform-admin sessions: the API sets app.platform_admin = 'on' only for an authenticated platform admin.
            CREATE OR REPLACE FUNCTION infra.is_platform_admin() RETURNS boolean
                LANGUAGE sql STABLE
            AS $$ SELECT coalesce(current_setting('app.platform_admin', true), '') = 'on' $$;
            REVOKE ALL ON FUNCTION infra.is_platform_admin() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION infra.is_platform_admin() TO handynet_app;

            -- IMEI blacklist: platform-wide, not tenant-owned. Rows are visible/writable only in platform-admin sessions;
            -- tenants get yes/no through the SECURITY DEFINER functions below. Never deleted.
            GRANT SELECT, INSERT, UPDATE ON inventory.imei_blacklist TO handynet_app;
            REVOKE DELETE, TRUNCATE ON inventory.imei_blacklist FROM handynet_app;
            ALTER TABLE inventory.imei_blacklist ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS platform_admin_only ON inventory.imei_blacklist;
            CREATE POLICY platform_admin_only ON inventory.imei_blacklist
                USING (infra.is_platform_admin()) WITH CHECK (infra.is_platform_admin());

            -- The only allowed change: an active entry is deactivated once (with who/when/why). Everything else is history.
            CREATE OR REPLACE FUNCTION inventory.imei_blacklist_guard() RETURNS trigger
                LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP <> 'UPDATE' THEN
                    RAISE EXCEPTION 'IMEI blacklist entries are never deleted; deactivate them instead' USING ERRCODE = '42501';
                END IF;
                IF OLD.deactivated_at IS NOT NULL THEN
                    RAISE EXCEPTION 'A deactivated blacklist entry is final' USING ERRCODE = '42501';
                END IF;
                IF NEW.deactivated_at IS NULL
                   OR NEW.id IS DISTINCT FROM OLD.id OR NEW.imei IS DISTINCT FROM OLD.imei OR NEW.reason IS DISTINCT FROM OLD.reason
                   OR NEW.note IS DISTINCT FROM OLD.note OR NEW.source IS DISTINCT FROM OLD.source
                   OR NEW.created_by_user_id IS DISTINCT FROM OLD.created_by_user_id OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'Only the deactivation of a blacklist entry can be recorded' USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            DROP TRIGGER IF EXISTS imei_blacklist_guard ON inventory.imei_blacklist;
            CREATE TRIGGER imei_blacklist_guard BEFORE UPDATE OR DELETE ON inventory.imei_blacklist
                FOR EACH ROW EXECUTE FUNCTION inventory.imei_blacklist_guard();
            DROP TRIGGER IF EXISTS imei_blacklist_no_truncate ON inventory.imei_blacklist;
            CREATE TRIGGER imei_blacklist_no_truncate BEFORE TRUNCATE ON inventory.imei_blacklist
                FOR EACH STATEMENT EXECUTE FUNCTION inventory.imei_blacklist_guard();

            -- Yes/no answers for tenants (like inventory.imei_in_stock): nothing else of the list is revealed.
            -- ::bpchar keeps the index usable.
            CREATE OR REPLACE FUNCTION inventory.imei_blacklisted(p_imei text) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$ SELECT EXISTS (SELECT 1 FROM inventory.imei_blacklist b WHERE b.imei = p_imei::bpchar AND b.deactivated_at IS NULL) $$;
            REVOKE ALL ON FUNCTION inventory.imei_blacklisted(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION inventory.imei_blacklisted(text) TO handynet_app;

            CREATE OR REPLACE FUNCTION inventory.imeis_blacklisted(p_imeis text[]) RETURNS SETOF text
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$ SELECT DISTINCT b.imei::text FROM inventory.imei_blacklist b WHERE b.imei = ANY (p_imeis::bpchar[]) AND b.deactivated_at IS NULL $$;
            REVOKE ALL ON FUNCTION inventory.imeis_blacklisted(text[]) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION inventory.imeis_blacklisted(text[]) TO handynet_app;

            CREATE OR REPLACE FUNCTION inventory.imeis_in_stock(p_imeis text[]) RETURNS SETOF text
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$ SELECT DISTINCT u.imei::text FROM inventory.stock_units u WHERE u.imei = ANY (p_imeis::bpchar[]) AND u.status IN ('InStock', 'Reserved') $$;
            REVOKE ALL ON FUNCTION inventory.imeis_in_stock(text[]) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION inventory.imeis_in_stock(text[]) TO handynet_app;

            -- Backstop for every code path: a blacklisted IMEI can neither enter stock (insert, return) nor be sold.
            -- Scrapping stays possible. SECURITY DEFINER so the check sees the list regardless of the session.
            CREATE OR REPLACE FUNCTION inventory.stock_units_not_blacklisted() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
            BEGIN
                IF NEW.status IN ('InStock', 'Reserved', 'Sold')
                   AND (TG_OP = 'INSERT' OR OLD.status IS DISTINCT FROM NEW.status)
                   AND EXISTS (SELECT 1 FROM inventory.imei_blacklist b WHERE b.imei = NEW.imei AND b.deactivated_at IS NULL) THEN
                    RAISE EXCEPTION 'IMEI % is blacklisted', NEW.imei USING ERRCODE = 'HNB01';
                END IF;
                RETURN NEW;
            END $$;
            REVOKE ALL ON FUNCTION inventory.stock_units_not_blacklisted() FROM PUBLIC;
            DROP TRIGGER IF EXISTS stock_units_not_blacklisted ON inventory.stock_units;
            CREATE TRIGGER stock_units_not_blacklisted BEFORE INSERT OR UPDATE OF status ON inventory.stock_units
                FOR EACH ROW EXECUTE FUNCTION inventory.stock_units_not_blacklisted();
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP TRIGGER IF EXISTS stock_units_not_blacklisted ON inventory.stock_units;
            DROP FUNCTION IF EXISTS inventory.stock_units_not_blacklisted();
            DROP FUNCTION IF EXISTS inventory.imeis_in_stock(text[]);
            DROP FUNCTION IF EXISTS inventory.imeis_blacklisted(text[]);
            DROP FUNCTION IF EXISTS inventory.imei_blacklisted(text);
            DROP TRIGGER IF EXISTS imei_blacklist_no_truncate ON inventory.imei_blacklist;
            DROP TRIGGER IF EXISTS imei_blacklist_guard ON inventory.imei_blacklist;
            DROP FUNCTION IF EXISTS inventory.imei_blacklist_guard();
            DROP POLICY IF EXISTS platform_admin_only ON inventory.imei_blacklist;
            DROP FUNCTION IF EXISTS infra.is_platform_admin();
            """);
}
