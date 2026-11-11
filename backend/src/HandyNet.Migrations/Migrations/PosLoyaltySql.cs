using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosLoyalty</c> (ADR-0020): tenant isolation and grants of <c>pos.loyalty_settings</c>,
/// <c>pos.loyalty_accounts</c> (never deleted) and <c>pos.loyalty_transactions</c> (append-only), and a deferred check that
/// an account's points equal the sum of its transactions at every commit.
/// </summary>
internal static class PosLoyaltySql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.loyalty_settings ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.loyalty_settings;
            CREATE POLICY tenant_isolation ON pos.loyalty_settings
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON pos.loyalty_settings TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.loyalty_settings FROM handynet_app;

            ALTER TABLE pos.loyalty_accounts ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.loyalty_accounts;
            CREATE POLICY tenant_isolation ON pos.loyalty_accounts
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON pos.loyalty_accounts TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.loyalty_accounts FROM handynet_app;

            ALTER TABLE pos.loyalty_transactions ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.loyalty_transactions;
            CREATE POLICY tenant_isolation ON pos.loyalty_transactions
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT ON pos.loyalty_transactions TO handynet_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON pos.loyalty_transactions FROM handynet_app;

            -- Transactions are append-only and accounts are never deleted, also for the owner.
            CREATE TRIGGER loyalty_transactions_immutable BEFORE UPDATE OR DELETE ON pos.loyalty_transactions
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER loyalty_transactions_no_truncate BEFORE TRUNCATE ON pos.loyalty_transactions
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER loyalty_accounts_no_delete BEFORE DELETE ON pos.loyalty_accounts
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER loyalty_accounts_no_truncate BEFORE TRUNCATE ON pos.loyalty_accounts
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

            -- At commit: points = sum of the customer's transactions (for every account and transaction the commit touched).
            CREATE FUNCTION pos.assert_loyalty_ledger(p_organization_id uuid, p_customer_id uuid) RETURNS void LANGUAGE plpgsql AS $$
            DECLARE
                balance bigint;
                ledger bigint;
            BEGIN
                SELECT a.points INTO balance FROM pos.loyalty_accounts a WHERE a.organization_id = p_organization_id AND a.customer_id = p_customer_id;
                SELECT COALESCE(sum(t.points), 0) INTO ledger FROM pos.loyalty_transactions t
                    WHERE t.organization_id = p_organization_id AND t.customer_id = p_customer_id;
                IF balance IS DISTINCT FROM ledger THEN
                    RAISE EXCEPTION 'loyalty account %: % points do not match its transactions (%)', p_customer_id, balance, ledger
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_loyalty_accounts_ledger';
                END IF;
            END $$;
            CREATE FUNCTION pos.check_loyalty_ledger() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pos.assert_loyalty_ledger(NEW.organization_id, NEW.customer_id);
                RETURN NULL;
            END $$;
            CREATE CONSTRAINT TRIGGER loyalty_accounts_ledger AFTER INSERT OR UPDATE ON pos.loyalty_accounts
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_loyalty_ledger();
            CREATE CONSTRAINT TRIGGER loyalty_transactions_ledger AFTER INSERT ON pos.loyalty_transactions
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_loyalty_ledger();
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP TRIGGER IF EXISTS loyalty_transactions_ledger ON pos.loyalty_transactions;
            DROP TRIGGER IF EXISTS loyalty_accounts_ledger ON pos.loyalty_accounts;
            DROP FUNCTION IF EXISTS pos.check_loyalty_ledger();
            DROP FUNCTION IF EXISTS pos.assert_loyalty_ledger(uuid, uuid);
            DROP TRIGGER IF EXISTS loyalty_accounts_no_truncate ON pos.loyalty_accounts;
            DROP TRIGGER IF EXISTS loyalty_accounts_no_delete ON pos.loyalty_accounts;
            DROP TRIGGER IF EXISTS loyalty_transactions_no_truncate ON pos.loyalty_transactions;
            DROP TRIGGER IF EXISTS loyalty_transactions_immutable ON pos.loyalty_transactions;
            DROP POLICY IF EXISTS tenant_isolation ON pos.loyalty_transactions;
            DROP POLICY IF EXISTS tenant_isolation ON pos.loyalty_accounts;
            DROP POLICY IF EXISTS tenant_isolation ON pos.loyalty_settings;
            """);
}
