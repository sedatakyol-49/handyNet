using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosCustomerDisplay</c> (ADR-0023): tenant isolation and grants of <c>pos.display_links</c>
/// (never deleted; only the revocation may change a row, once) and the SECURITY DEFINER function the anonymous display
/// endpoint resolves a token hash with. It returns nothing but the register, its organization and the organization's name.
/// </summary>
internal static class PosCustomerDisplaySql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.display_links ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.display_links;
            CREATE POLICY tenant_isolation ON pos.display_links
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON pos.display_links TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.display_links FROM handynet_app;

            -- Only the revocation may change a link, and only once: a revoked link never becomes valid again.
            CREATE FUNCTION pos.guard_display_link() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP <> 'UPDATE' THEN
                    RAISE EXCEPTION 'pos.display_links rows are never deleted' USING ERRCODE = '42501';
                END IF;
                IF OLD.revoked_at IS NOT NULL OR NEW.revoked_at IS NULL
                   OR NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.register_id <> OLD.register_id
                   OR NEW.token_hash <> OLD.token_hash OR NEW.created_at <> OLD.created_at OR NEW.expires_at <> OLD.expires_at
                   OR NEW.created_by <> OLD.created_by THEN
                    RAISE EXCEPTION 'a display link can only be revoked, once' USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER display_links_guard BEFORE UPDATE OR DELETE ON pos.display_links
                FOR EACH ROW EXECUTE FUNCTION pos.guard_display_link();
            CREATE TRIGGER display_links_no_truncate BEFORE TRUNCATE ON pos.display_links
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

            -- Anonymous lookup of one active link by its token hash (no tenant context exists on the public endpoint).
            CREATE FUNCTION pos.public_display(p_token_hash text)
                RETURNS TABLE (register_id uuid, organization_id uuid, organization_name text, expires_at timestamptz)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT l.register_id, l.organization_id, o.name::text, l.expires_at
                FROM pos.display_links l
                JOIN tenancy.organizations o ON o.id = l.organization_id
                WHERE l.token_hash = p_token_hash AND l.revoked_at IS NULL AND l.expires_at > now()
                LIMIT 1
            $$;
            REVOKE ALL ON FUNCTION pos.public_display(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION pos.public_display(text) TO handynet_app;
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP FUNCTION IF EXISTS pos.public_display(text);
            DROP TRIGGER IF EXISTS display_links_no_truncate ON pos.display_links;
            DROP TRIGGER IF EXISTS display_links_guard ON pos.display_links;
            DROP FUNCTION IF EXISTS pos.guard_display_link();
            DROP POLICY IF EXISTS tenant_isolation ON pos.display_links;
            """);
}
