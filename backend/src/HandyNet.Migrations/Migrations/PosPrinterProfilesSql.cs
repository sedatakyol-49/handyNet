using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosPrinterProfiles</c> (ADR-0018): tenant isolation and grants of <c>pos.printer_profiles</c>.
/// Printer profiles are configuration, not business records: the app role may delete them (every change is in the audit log).
/// </summary>
internal static class PosPrinterProfilesSql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.printer_profiles ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON pos.printer_profiles
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE, DELETE ON pos.printer_profiles TO handynet_app;
            REVOKE TRUNCATE ON pos.printer_profiles FROM handynet_app;
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("DROP POLICY IF EXISTS tenant_isolation ON pos.printer_profiles;");
}
