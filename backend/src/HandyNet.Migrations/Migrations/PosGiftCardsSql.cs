using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PosGiftCards</c> (ADR-0020): tenant isolation and grants of <c>pos.gift_cards</c> (never
/// deleted; identity and initial amount immutable) and <c>pos.gift_card_transactions</c> (append-only), and a deferred
/// check that a card's balance equals the sum of its transactions at every commit — so a balance can neither be changed
/// without a transaction nor a transaction be written without moving the balance.
/// </summary>
internal static class PosGiftCardsSql
{
    public static void Up(MigrationBuilder m) =>
        m.Sql("""
            ALTER TABLE pos.gift_cards ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.gift_cards;
            CREATE POLICY tenant_isolation ON pos.gift_cards
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT, UPDATE ON pos.gift_cards TO handynet_app;
            REVOKE DELETE, TRUNCATE ON pos.gift_cards FROM handynet_app;

            ALTER TABLE pos.gift_card_transactions ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON pos.gift_card_transactions;
            CREATE POLICY tenant_isolation ON pos.gift_card_transactions
                USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
            GRANT SELECT, INSERT ON pos.gift_card_transactions TO handynet_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON pos.gift_card_transactions FROM handynet_app;

            -- Transactions are append-only, also for the owner.
            CREATE TRIGGER gift_card_transactions_immutable BEFORE UPDATE OR DELETE ON pos.gift_card_transactions
                FOR EACH ROW EXECUTE FUNCTION pos.forbid_change();
            CREATE TRIGGER gift_card_transactions_no_truncate BEFORE TRUNCATE ON pos.gift_card_transactions
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

            -- A card is never deleted; what it was issued with never changes; a voided card stays voided.
            CREATE FUNCTION pos.guard_gift_card() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP <> 'UPDATE' THEN
                    RAISE EXCEPTION 'pos.gift_cards rows are never deleted' USING ERRCODE = '42501';
                END IF;
                IF NEW.id <> OLD.id OR NEW.organization_id <> OLD.organization_id OR NEW.initial_minor <> OLD.initial_minor
                   OR NEW.issued_at <> OLD.issued_at OR NEW.issued_receipt_id <> OLD.issued_receipt_id
                   OR NEW.expires_on IS DISTINCT FROM OLD.expires_on THEN
                    RAISE EXCEPTION 'identity, initial amount and expiry of a gift card cannot change' USING ERRCODE = '42501';
                END IF;
                IF OLD.status = 'Voided' AND (NEW.status <> 'Voided' OR NEW.balance_minor <> 0 OR NEW.code_hash <> OLD.code_hash) THEN
                    RAISE EXCEPTION 'a voided gift card is final' USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER gift_cards_guard BEFORE UPDATE OR DELETE ON pos.gift_cards
                FOR EACH ROW EXECUTE FUNCTION pos.guard_gift_card();
            CREATE TRIGGER gift_cards_no_truncate BEFORE TRUNCATE ON pos.gift_cards
                FOR EACH STATEMENT EXECUTE FUNCTION pos.forbid_change();

            -- At commit: balance = sum of the card's transactions (checked for every card and transaction the commit touched).
            CREATE FUNCTION pos.assert_gift_card_ledger(p_card_id uuid) RETURNS void LANGUAGE plpgsql AS $$
            DECLARE
                balance bigint;
                ledger bigint;
            BEGIN
                SELECT c.balance_minor INTO balance FROM pos.gift_cards c WHERE c.id = p_card_id;
                SELECT COALESCE(sum(t.amount_minor), 0) INTO ledger FROM pos.gift_card_transactions t WHERE t.gift_card_id = p_card_id;
                IF balance IS DISTINCT FROM ledger THEN
                    RAISE EXCEPTION 'gift card %: balance % does not match its transactions (%)', p_card_id, balance, ledger
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_gift_cards_ledger';
                END IF;
            END $$;
            CREATE FUNCTION pos.check_gift_card_ledger() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pos.assert_gift_card_ledger(NEW.id);
                RETURN NULL;
            END $$;
            CREATE FUNCTION pos.check_gift_card_transaction_ledger() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pos.assert_gift_card_ledger(NEW.gift_card_id);
                RETURN NULL;
            END $$;
            CREATE CONSTRAINT TRIGGER gift_cards_ledger AFTER INSERT OR UPDATE ON pos.gift_cards
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_gift_card_ledger();
            CREATE CONSTRAINT TRIGGER gift_card_transactions_ledger AFTER INSERT ON pos.gift_card_transactions
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pos.check_gift_card_transaction_ledger();
            """);

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP TRIGGER IF EXISTS gift_card_transactions_ledger ON pos.gift_card_transactions;
            DROP TRIGGER IF EXISTS gift_cards_ledger ON pos.gift_cards;
            DROP FUNCTION IF EXISTS pos.check_gift_card_transaction_ledger();
            DROP FUNCTION IF EXISTS pos.check_gift_card_ledger();
            DROP FUNCTION IF EXISTS pos.assert_gift_card_ledger(uuid);
            DROP TRIGGER IF EXISTS gift_cards_no_truncate ON pos.gift_cards;
            DROP TRIGGER IF EXISTS gift_cards_guard ON pos.gift_cards;
            DROP FUNCTION IF EXISTS pos.guard_gift_card();
            DROP TRIGGER IF EXISTS gift_card_transactions_no_truncate ON pos.gift_card_transactions;
            DROP TRIGGER IF EXISTS gift_card_transactions_immutable ON pos.gift_card_transactions;
            DROP POLICY IF EXISTS tenant_isolation ON pos.gift_card_transactions;
            DROP POLICY IF EXISTS tenant_isolation ON pos.gift_cards;
            """);
}
