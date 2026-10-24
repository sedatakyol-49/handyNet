using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>AnkaufCheckInSignaturePayout</c> (ADR-0014): grants, Row-Level Security and guard triggers for
/// self check-ins and seller signatures, the public check-in functions (SECURITY DEFINER, by token hash only), the purge
/// finder for the retention job, and the extended intake guard (new columns are identity or fixed once closed).
/// </summary>
internal static class AnkaufCheckInSignaturePayoutSql
{
    private static readonly string[] TenantTables = ["checkins", "intake_signatures"];

    public static void Up(MigrationBuilder m)
    {
        // ---- Grants: both tables are updated in narrow, trigger-checked ways and never deleted --------------------------------
        m.Sql("""
            GRANT SELECT, INSERT, UPDATE ON ankauf.checkins, ankauf.intake_signatures TO handynet_app;
            REVOKE DELETE, TRUNCATE ON ankauf.checkins, ankauf.intake_signatures FROM handynet_app;
            """);

        foreach (var table in TenantTables)
            m.Sql($"""
                ALTER TABLE ankauf.{table} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON ankauf.{table}
                    USING (organization_id = infra.current_org()) WITH CHECK (organization_id = infra.current_org());
                """);

        // ---- Intakes: capture method and payout consistency ------------------------------------------------------------------
        m.Sql("""
            ALTER TABLE ankauf.intakes ALTER COLUMN capture_method DROP DEFAULT;
            ALTER TABLE ankauf.intakes ADD CONSTRAINT ck_intakes_capture_method
                CHECK (capture_method IN ('Manual', 'Mrz', 'SelfCheckIn') AND (capture_method <> 'SelfCheckIn' OR check_in_id IS NOT NULL));
            ALTER TABLE ankauf.intakes ADD CONSTRAINT ck_intakes_cash_payout
                CHECK ((cash_movement_id IS NULL) = (cash_register_id IS NULL) AND (cash_movement_id IS NULL OR payment_method = 'Cash'));
            """);

        // ---- Intake guard: the new columns are identity (capture, check-in) or fixed with the closed intake -------------------
        m.Sql(IntakeGuard(withNewColumns: true));

        // ---- Check-ins: identity fixed; submitted once; data only removed (consumed/purged), never changed or restored ---------
        m.Sql("""
            CREATE FUNCTION ankauf.guard_checkin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'check-ins are never deleted' USING ERRCODE = '42501';
                END IF;
                IF (NEW.id, NEW.organization_id, NEW.token_hash, NEW.shop_name, NEW.created_at, NEW.created_by, NEW.expires_at)
                   IS DISTINCT FROM (OLD.id, OLD.organization_id, OLD.token_hash, OLD.shop_name, OLD.created_at, OLD.created_by, OLD.expires_at) THEN
                    RAISE EXCEPTION 'check-in identity is immutable' USING ERRCODE = '42501';
                END IF;
                IF OLD.submitted_at IS NOT NULL AND NEW.submitted_at IS DISTINCT FROM OLD.submitted_at THEN
                    RAISE EXCEPTION 'a check-in is submitted once' USING ERRCODE = '42501';
                END IF;
                IF NEW.submitted_at IS NULL AND (NEW.seller IS NOT NULL OR NEW.signature_image IS NOT NULL OR NEW.signed_at IS NOT NULL OR NEW.consumed_at IS NOT NULL) THEN
                    RAISE EXCEPTION 'check-in data comes only with the submission' USING ERRCODE = '42501';
                END IF;
                IF OLD.submitted_at IS NOT NULL AND (
                       (NEW.seller IS NOT NULL AND NEW.seller IS DISTINCT FROM OLD.seller)
                    OR (NEW.signature_image IS NOT NULL AND NEW.signature_image IS DISTINCT FROM OLD.signature_image)
                    OR NEW.signed_at IS DISTINCT FROM OLD.signed_at) THEN
                    RAISE EXCEPTION 'submitted check-in data can only be removed' USING ERRCODE = '42501';
                END IF;
                IF ((OLD.seller IS NOT NULL AND NEW.seller IS NULL) OR (OLD.signature_image IS NOT NULL AND NEW.signature_image IS NULL))
                   AND NEW.purged_at IS NULL THEN
                    RAISE EXCEPTION 'check-in data is removed only together with purged_at' USING ERRCODE = '42501';
                END IF;
                IF (OLD.consumed_at IS NOT NULL AND (NEW.consumed_at, NEW.intake_id) IS DISTINCT FROM (OLD.consumed_at, OLD.intake_id))
                   OR (OLD.purged_at IS NOT NULL AND NEW.purged_at IS DISTINCT FROM OLD.purged_at) THEN
                    RAISE EXCEPTION 'check-in consumption and purge are final' USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER checkins_guard BEFORE UPDATE OR DELETE ON ankauf.checkins
                FOR EACH ROW EXECUTE FUNCTION ankauf.guard_checkin();
            """);

        // ---- Signatures: append-only; the only change is the one-way anonymization (image removed) -----------------------------
        m.Sql("""
            CREATE FUNCTION ankauf.guard_intake_signature() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'signatures are never deleted' USING ERRCODE = '42501';
                END IF;
                IF OLD.anonymized_at IS NOT NULL
                   OR (NEW.id, NEW.organization_id, NEW.intake_id, NEW.purpose, NEW.source, NEW.image_sha256, NEW.data_hash, NEW.binding_hash,
                       NEW.declaration_version, NEW.agreed_paid_minor, NEW.agreed_payment_method, NEW.signed_at, NEW.created_by)
                   IS DISTINCT FROM (OLD.id, OLD.organization_id, OLD.intake_id, OLD.purpose, OLD.source, OLD.image_sha256, OLD.data_hash, OLD.binding_hash,
                       OLD.declaration_version, OLD.agreed_paid_minor, OLD.agreed_payment_method, OLD.signed_at, OLD.created_by)
                   OR NEW.image IS NOT NULL OR NEW.anonymized_at IS NULL THEN
                    RAISE EXCEPTION 'signature % is fixed; only its image can be removed by anonymization', OLD.id USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER intake_signatures_guard BEFORE UPDATE OR DELETE ON ankauf.intake_signatures
                FOR EACH ROW EXECUTE FUNCTION ankauf.guard_intake_signature();
            """);

        // ---- Public self check-in (anonymous): the only cross-tenant paths, by the SHA-256 of an unguessable token -------------
        m.Sql("""
            CREATE FUNCTION ankauf.checkin_public(p_token_hash text)
                RETURNS TABLE (shop_name text, expires_at timestamptz, submitted boolean)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT c.shop_name::text, c.expires_at, c.submitted_at IS NOT NULL
                FROM ankauf.checkins c
                WHERE c.token_hash = p_token_hash AND c.consumed_at IS NULL AND c.purged_at IS NULL
                LIMIT 1
            $$;

            -- 1 = written, 2 = already submitted, 0 = unknown, expired, used or purged. Single use: written only once, before expiry.
            CREATE FUNCTION ankauf.checkin_submit(p_token_hash text, p_seller jsonb, p_signature bytea, p_signed_at timestamptz)
                RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                r record;
            BEGIN
                IF p_seller IS NULL THEN
                    RETURN 0;
                END IF;
                SELECT c.id, c.submitted_at, c.expires_at, c.consumed_at, c.purged_at INTO r
                FROM ankauf.checkins c WHERE c.token_hash = p_token_hash FOR UPDATE;
                IF NOT FOUND OR r.consumed_at IS NOT NULL OR r.purged_at IS NOT NULL THEN
                    RETURN 0;
                END IF;
                IF r.submitted_at IS NOT NULL THEN
                    RETURN 2;
                END IF;
                IF r.expires_at <= now() THEN
                    RETURN 0;
                END IF;
                UPDATE ankauf.checkins
                SET seller = p_seller, signature_image = p_signature,
                    signed_at = CASE WHEN p_signature IS NULL THEN NULL ELSE coalesce(p_signed_at, now()) END,
                    submitted_at = now()
                WHERE id = r.id;
                RETURN 1;
            END $$;

            -- Retention job: unused check-ins expired before the cutoff that still hold (or may hold) personal data.
            CREATE FUNCTION ankauf.checkins_due_for_purge(p_cutoff timestamptz, p_limit integer)
                RETURNS TABLE (id uuid, organization_id uuid)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT c.id, c.organization_id FROM ankauf.checkins c
                WHERE c.purged_at IS NULL AND c.consumed_at IS NULL AND c.expires_at < p_cutoff
                ORDER BY c.expires_at
                LIMIT p_limit
            $$;

            REVOKE ALL ON FUNCTION ankauf.checkin_public(text) FROM PUBLIC;
            REVOKE ALL ON FUNCTION ankauf.checkin_submit(text, jsonb, bytea, timestamptz) FROM PUBLIC;
            REVOKE ALL ON FUNCTION ankauf.checkins_due_for_purge(timestamptz, integer) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION ankauf.checkin_public(text) TO handynet_app;
            GRANT EXECUTE ON FUNCTION ankauf.checkin_submit(text, jsonb, bytea, timestamptz) TO handynet_app;
            GRANT EXECUTE ON FUNCTION ankauf.checkins_due_for_purge(timestamptz, integer) TO handynet_app;
            """);
    }

    public static void Down(MigrationBuilder m)
    {
        m.Sql("""
            DROP FUNCTION IF EXISTS ankauf.checkins_due_for_purge(timestamptz, integer);
            DROP FUNCTION IF EXISTS ankauf.checkin_submit(text, jsonb, bytea, timestamptz);
            DROP FUNCTION IF EXISTS ankauf.checkin_public(text);
            DROP TRIGGER IF EXISTS intake_signatures_guard ON ankauf.intake_signatures;
            DROP FUNCTION IF EXISTS ankauf.guard_intake_signature();
            DROP TRIGGER IF EXISTS checkins_guard ON ankauf.checkins;
            DROP FUNCTION IF EXISTS ankauf.guard_checkin();
            ALTER TABLE ankauf.intakes DROP CONSTRAINT IF EXISTS ck_intakes_capture_method;
            ALTER TABLE ankauf.intakes DROP CONSTRAINT IF EXISTS ck_intakes_cash_payout;
            """);
        m.Sql(IntakeGuard(withNewColumns: false));
    }

    /// <summary>The intake guard of ADR-0009, optionally extended by the columns of this migration.</summary>
    private static string IntakeGuard(bool withNewColumns)
    {
        var identity = withNewColumns ? ", capture_method, check_in_id" : "";
        var closed = withNewColumns ? ", signature_id, cash_movement_id, cash_register_id" : "";
        string Cols(string prefix, string list) => string.Join(", ", list.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(c => prefix + c.Trim()));
        var identityCols = "id, organization_id, quote_id, imei, received_at, received_by" + identity;
        var closedCols = "status, inspected_condition, inspected_defects, inspected_battery_health_percent, inspection_notes, inspected_offer_minor, "
                         + "inspected_max_consumer_price_minor, inspected_at, paid_minor, payment_method, stock_unit_id, completed_at, rejection_reason" + closed;
        return $"""
            CREATE OR REPLACE FUNCTION ankauf.guard_intake() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'intakes are never deleted' USING ERRCODE = '42501';
                END IF;
                IF ({Cols("NEW.", identityCols)}) IS DISTINCT FROM ({Cols("OLD.", identityCols)}) THEN
                    RAISE EXCEPTION 'intake identity columns are immutable' USING ERRCODE = '42501';
                END IF;
                IF OLD.anonymized_at IS NOT NULL AND (NEW.anonymized_at IS DISTINCT FROM OLD.anonymized_at OR NEW.seller IS DISTINCT FROM OLD.seller) THEN
                    RAISE EXCEPTION 'anonymized seller data cannot be changed' USING ERRCODE = '42501';
                END IF;
                IF NEW.seller IS DISTINCT FROM OLD.seller AND NEW.anonymized_at IS NULL THEN
                    RAISE EXCEPTION 'seller data can only be changed by anonymization' USING ERRCODE = '42501';
                END IF;
                IF (OLD.status IN ('Completed', 'Rejected') OR (OLD.anonymized_at IS NULL AND NEW.anonymized_at IS NOT NULL))
                   AND ({Cols("NEW.", closedCols)})
                   IS DISTINCT FROM ({Cols("OLD.", closedCols)}) THEN
                    RAISE EXCEPTION 'closed intakes change only by anonymization, and anonymization changes nothing else' USING ERRCODE = '42501';
                END IF;
                RETURN NEW;
            END $$;
            """;
    }
}
