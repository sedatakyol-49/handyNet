using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>CompanyProfileAndLogo</c> (ADR-0014): RLS and grants for the company logos, an UPDATE policy
/// on <c>tenancy.organizations</c> (a tenant session may change only its own organization row), the kind of a status-history
/// entry, and <c>pos.public_receipt</c> returning the issuing organization (for the logo on the public e-receipt).
/// </summary>
internal static class CompanyProfileAndLogoSql
{
    public static void Up(MigrationBuilder m)
    {
        // ---- Logos: everyone reads (printed on receipts, public e-receipts), only the own organization writes; no DELETE ---
        m.Sql("""
            ALTER TABLE tenancy.organization_logos ENABLE ROW LEVEL SECURITY;
            CREATE POLICY read_all ON tenancy.organization_logos FOR SELECT USING (true);
            CREATE POLICY own_inserts ON tenancy.organization_logos FOR INSERT
                WITH CHECK (organization_id = infra.current_org());
            CREATE POLICY own_updates ON tenancy.organization_logos FOR UPDATE
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON tenancy.organization_logos TO handynet_app;
            REVOKE DELETE, TRUNCATE ON tenancy.organization_logos FROM handynet_app;

            -- Only the removal may change a logo row, and only once (the files behind an id never change).
            CREATE FUNCTION tenancy.guard_organization_logo() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD.removed_at IS NOT NULL THEN
                    RAISE EXCEPTION 'a removed logo is final' USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_organization_logos_final';
                END IF;
                IF (NEW.id, NEW.organization_id, NEW.screen_storage_key, NEW.print_storage_key, NEW.sha256, NEW.uploaded_by, NEW.uploaded_at,
                    NEW.screen_width, NEW.screen_height, NEW.print_width, NEW.print_height)
                   IS DISTINCT FROM
                   (OLD.id, OLD.organization_id, OLD.screen_storage_key, OLD.print_storage_key, OLD.sha256, OLD.uploaded_by, OLD.uploaded_at,
                    OLD.screen_width, OLD.screen_height, OLD.print_width, OLD.print_height) THEN
                    RAISE EXCEPTION 'logo files and identity are immutable' USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_organization_logos_final';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER organization_logos_guard BEFORE UPDATE ON tenancy.organization_logos
                FOR EACH ROW EXECUTE FUNCTION tenancy.guard_organization_logo();
            """);

        // ---- Organizations: readable by everyone as before; a tenant session may update only its own row ---------------------
        // Platform administration and registration run without a tenant (current_org() IS NULL) and are not affected.
        // No DELETE policy: the app role cannot delete organizations.
        m.Sql("""
            ALTER TABLE tenancy.organizations ENABLE ROW LEVEL SECURITY;
            CREATE POLICY read_all ON tenancy.organizations FOR SELECT USING (true);
            CREATE POLICY register ON tenancy.organizations FOR INSERT WITH CHECK (true);
            CREATE POLICY own_or_platform_updates ON tenancy.organizations FOR UPDATE
                USING (infra.current_org() IS NULL OR id = infra.current_org())
                WITH CHECK (infra.current_org() IS NULL OR id = infra.current_org());
            """);

        // ---- Status history: kind of entry ----------------------------------------------------------------------------------
        m.Sql("""
            ALTER TABLE tenancy.organization_status_changes
                ADD CONSTRAINT ck_organization_status_changes_kind CHECK (
                    kind = 'StatusChange' OR (kind = 'LegalDetailsChanged' AND from_status = to_status));
            """);

        // ---- Public e-receipt: also return the issuing organization (logo and receipt texts) --------------------------------
        m.Sql("""
            DROP FUNCTION pos.public_receipt(text);
            CREATE FUNCTION pos.public_receipt(p_token_hash text)
                RETURNS TABLE (document jsonb, cancelled boolean, expires_at timestamptz, organization_id uuid)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT r.document,
                       EXISTS (SELECT 1 FROM pos.receipts s WHERE s.referenced_receipt_id = r.id AND s.kind = 'Storno'),
                       l.expires_at,
                       r.organization_id
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
            DROP FUNCTION IF EXISTS pos.public_receipt(text);
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

            ALTER TABLE tenancy.organization_status_changes DROP CONSTRAINT IF EXISTS ck_organization_status_changes_kind;

            DROP POLICY IF EXISTS own_or_platform_updates ON tenancy.organizations;
            DROP POLICY IF EXISTS register ON tenancy.organizations;
            DROP POLICY IF EXISTS read_all ON tenancy.organizations;
            ALTER TABLE tenancy.organizations DISABLE ROW LEVEL SECURITY;

            DROP TRIGGER IF EXISTS organization_logos_guard ON tenancy.organization_logos;
            DROP FUNCTION IF EXISTS tenancy.guard_organization_logo();
            """);
    }
}
