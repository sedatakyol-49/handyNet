using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosCardTerminals</c> (ADR-0021): tenant isolation and grants of <c>pos.terminal_profiles</c>
/// (configuration: DELETE allowed, audited) and <c>pos.card_transactions</c> (business records: never deleted; a guard
/// trigger makes final results immutable and lets a transaction be linked to a receipt, or reversed, exactly once).
/// </summary>
internal static class PosCardTerminalsSql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.terminal_profiles ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.terminal_profiles;
            CREATE POLICY tenant_isolation ON pos.terminal_profiles
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE, DELETE ON pos.terminal_profiles TO handynet_app;
            REVOKE TRUNCATE ON pos.terminal_profiles FROM handynet_app;

            ALTER TABLE pos.card_transactions ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.card_transactions;
            CREATE POLICY tenant_isolation ON pos.card_transactions
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON pos.card_transactions TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.card_transactions FROM handynet_app;

            -- A card transaction is written as Pending, gets its final result once, and may then only be linked to one
            -- receipt (Approved, once) or reversed (Approved and not linked, once). Applies to every role, also the owner.
            CREATE FUNCTION pos.guard_card_transaction() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP <> 'UPDATE' THEN
                    RAISE EXCEPTION 'pos.card_transactions rows are never deleted' USING ERRCODE = '42501';
                END IF;
                IF NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.register_id <> OLD.register_id
                   OR NEW.kind <> OLD.kind OR NEW.amount_minor <> OLD.amount_minor OR NEW.created_at <> OLD.created_at
                   OR NEW.created_by <> OLD.created_by OR NEW.is_test <> OLD.is_test
                   OR NEW.terminal_profile_id IS DISTINCT FROM OLD.terminal_profile_id
                   OR NEW.terminal_name IS DISTINCT FROM OLD.terminal_name THEN
                    RAISE EXCEPTION 'identity, kind and amount of a card transaction cannot change' USING ERRCODE = '42501';
                END IF;
                IF OLD.status = 'Pending' THEN
                    IF NEW.status NOT IN ('Approved', 'Declined', 'Aborted', 'Failed') OR NEW.receipt_id IS NOT NULL THEN
                        RAISE EXCEPTION 'a pending card transaction can only receive its final result' USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END IF;
                -- Final: the terminal's result never changes.
                IF NEW.terminal_id IS DISTINCT FROM OLD.terminal_id OR NEW.trace_number IS DISTINCT FROM OLD.trace_number
                   OR NEW.terminal_receipt_number IS DISTINCT FROM OLD.terminal_receipt_number OR NEW.card_name IS DISTINCT FROM OLD.card_name
                   OR NEW.masked_pan IS DISTINCT FROM OLD.masked_pan OR NEW.auth_code IS DISTINCT FROM OLD.auth_code
                   OR NEW.result_code IS DISTINCT FROM OLD.result_code OR NEW.result_text IS DISTINCT FROM OLD.result_text
                   OR NEW.customer_receipt_text IS DISTINCT FROM OLD.customer_receipt_text
                   OR NEW.merchant_receipt_text IS DISTINCT FROM OLD.merchant_receipt_text
                   OR NEW.completed_at IS DISTINCT FROM OLD.completed_at THEN
                    RAISE EXCEPTION 'the result of a card transaction is final' USING ERRCODE = '42501';
                END IF;
                IF OLD.status = 'Approved' AND OLD.receipt_id IS NULL THEN
                    -- link to the receipt that uses it …
                    IF NEW.status = 'Approved' AND NEW.receipt_id IS NOT NULL AND NEW.reversed_at IS NULL AND NEW.reversal_text IS NULL THEN
                        RETURN NEW;
                    END IF;
                    -- … or reversal on the terminal.
                    IF NEW.status = 'Reversed' AND NEW.receipt_id IS NULL AND NEW.linked_at IS NULL THEN
                        RETURN NEW;
                    END IF;
                END IF;
                RAISE EXCEPTION 'card transaction % is final (%): it cannot be changed, relinked or reversed', OLD.id, OLD.status
                    USING ERRCODE = '42501';
            END $$;
            CREATE TRIGGER card_transactions_guard BEFORE UPDATE OR DELETE ON pos.card_transactions
                FOR EACH ROW EXECUTE FUNCTION pos.guard_card_transaction();
            CREATE TRIGGER card_transactions_no_truncate BEFORE TRUNCATE ON pos.card_transactions
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP TRIGGER IF EXISTS card_transactions_no_truncate ON pos.card_transactions;
            DROP TRIGGER IF EXISTS card_transactions_guard ON pos.card_transactions;
            DROP FUNCTION IF EXISTS pos.guard_card_transaction();
            DROP POLICY IF EXISTS tenant_isolation ON pos.card_transactions;
            DROP POLICY IF EXISTS tenant_isolation ON pos.terminal_profiles;
            """);
}
