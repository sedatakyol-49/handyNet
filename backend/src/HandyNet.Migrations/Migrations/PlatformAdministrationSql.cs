using Microsoft.EntityFrameworkCore.Migrations;

namespace HandyNet.Migrations.Migrations;

/// <summary>
/// Raw SQL of the migration <c>PlatformAdministration</c> (ADR-0013): append-only status history (with backfill from the
/// audit log), the "an organization keeps an owner" and "one active platform admin remains" invariants as deferred
/// constraint triggers, the audited membership removal and the id-only count functions for the company administration.
/// </summary>
internal static class PlatformAdministrationSql
{
    public static void Up(MigrationBuilder m)
    {
        // ---- Status history: append-only --------------------------------------------------------------------------------
        m.Sql("""
            GRANT SELECT, INSERT ON tenancy.organization_status_changes TO handynet_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON tenancy.organization_status_changes FROM handynet_app;
            """);

        // Backfill from the audit log: registration (Added) and every audited status change. Enum values were serialized as
        // numbers (declaration order of OrganizationStatus) or, possibly, as strings — both are handled.
        m.Sql("""
            WITH entries AS (
                SELECT a.occurred_at, a.user_id, a.action, o.id AS organization_id, o.created_by,
                       CASE WHEN a.action = 'Added' THEN NULL ELSE a.changes -> 'Status' -> 'from' END AS from_json,
                       CASE WHEN a.action = 'Added' THEN a.changes -> 'Status' ELSE a.changes -> 'Status' -> 'to' END AS to_json,
                       CASE WHEN a.action = 'Added' THEN NULL ELSE a.changes -> 'StatusReason' ->> 'to' END AS reason
                  FROM infra.audit_log a
                  JOIN tenancy.organizations o ON o.id::text = a.entity_id
                 WHERE a.entity_type = 'Organization'
                   AND (a.action = 'Added' OR (a.action = 'Modified' AND a.changes ? 'Status'))
            ), named AS (
                SELECT e.*,
                       CASE jsonb_typeof(e.from_json)
                           WHEN 'number' THEN (ARRAY['PendingVerification','Verified','Rejected','Suspended'])[(e.from_json #>> '{}')::int + 1]
                           WHEN 'string' THEN e.from_json #>> '{}' END AS from_status,
                       CASE jsonb_typeof(e.to_json)
                           WHEN 'number' THEN (ARRAY['PendingVerification','Verified','Rejected','Suspended'])[(e.to_json #>> '{}')::int + 1]
                           WHEN 'string' THEN e.to_json #>> '{}' END AS to_status
                  FROM entries e
            )
            INSERT INTO tenancy.organization_status_changes (id, organization_id, from_status, to_status, reason, changed_by, changed_at)
            SELECT gen_random_uuid(), n.organization_id, n.from_status, n.to_status, left(n.reason, 1000),
                   CASE WHEN n.action = 'Added' THEN COALESCE(n.user_id, n.created_by) ELSE n.user_id END,
                   n.occurred_at
              FROM named n
             WHERE n.to_status IS NOT NULL
               AND (n.user_id IS NULL OR EXISTS (SELECT 1 FROM identity.users u WHERE u.id = n.user_id));

            -- Organizations without an audited registration still get a first entry.
            INSERT INTO tenancy.organization_status_changes (id, organization_id, from_status, to_status, reason, changed_by, changed_at)
            SELECT gen_random_uuid(), o.id, NULL, 'PendingVerification', NULL, o.created_by, o.created_at
              FROM tenancy.organizations o
             WHERE NOT EXISTS (SELECT 1 FROM tenancy.organization_status_changes c WHERE c.organization_id = o.id AND c.from_status IS NULL);

            -- Where the history does not end in the current status (changes made before auditing), record the current one.
            INSERT INTO tenancy.organization_status_changes (id, organization_id, from_status, to_status, reason, changed_by, changed_at)
            SELECT gen_random_uuid(), o.id, last.to_status, o.status, o.status_reason,
                   CASE WHEN o.status = 'Verified' THEN o.verified_by END,
                   GREATEST(last.changed_at, COALESCE(CASE WHEN o.status = 'Verified' THEN o.verified_at END, last.changed_at))
              FROM tenancy.organizations o
              CROSS JOIN LATERAL (SELECT c.to_status, c.changed_at FROM tenancy.organization_status_changes c
                                   WHERE c.organization_id = o.id ORDER BY c.changed_at DESC, c.id DESC LIMIT 1) last
             WHERE last.to_status <> o.status;
            """);

        // ---- Every organization keeps at least one owner ------------------------------------------------------------------
        // Deferred to the end of the transaction, so a transfer (new owner up, old owner down) can run in any order. The
        // advisory lock per organization serializes concurrent demotions (no write skew between two transactions).
        m.Sql("""
            CREATE FUNCTION tenancy.ensure_owner_remains() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD.role = 'Owner' AND (TG_OP = 'DELETE' OR NEW.role <> 'Owner' OR NEW.organization_id <> OLD.organization_id) THEN
                    PERFORM pg_advisory_xact_lock(hashtextextended('tenancy.owner:' || OLD.organization_id::text, 0));
                    IF EXISTS (SELECT 1 FROM tenancy.organizations o WHERE o.id = OLD.organization_id)
                       AND NOT EXISTS (SELECT 1 FROM tenancy.memberships x WHERE x.organization_id = OLD.organization_id AND x.role = 'Owner') THEN
                        RAISE EXCEPTION 'organization % must keep at least one owner', OLD.organization_id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_memberships_owner_required';
                    END IF;
                END IF;
                RETURN NULL;
            END $$;

            CREATE CONSTRAINT TRIGGER owner_required AFTER UPDATE OR DELETE ON tenancy.memberships
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION tenancy.ensure_owner_remains();
            """);

        // ---- At least one active platform admin remains --------------------------------------------------------------------
        m.Sql("""
            CREATE FUNCTION identity.ensure_platform_admin_remains() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD.is_platform_admin AND OLD.is_active AND NOT (NEW.is_platform_admin AND NEW.is_active) THEN
                    PERFORM pg_advisory_xact_lock(hashtextextended('identity.platform_admins', 0));
                    IF NOT EXISTS (SELECT 1 FROM identity.users u WHERE u.is_platform_admin AND u.is_active) THEN
                        RAISE EXCEPTION 'at least one active platform administrator must remain'
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_users_last_platform_admin';
                    END IF;
                END IF;
                RETURN NULL;
            END $$;

            CREATE CONSTRAINT TRIGGER platform_admin_required AFTER UPDATE OF is_platform_admin, is_active ON identity.users
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION identity.ensure_platform_admin_remains();
            """);

        // ---- Membership removal: the only DELETE path, platform admin only, audited in the same statement -----------------
        m.Sql("""
            CREATE FUNCTION tenancy.admin_remove_membership(p_organization_id uuid, p_user_id uuid, p_admin uuid, p_reason text, p_now timestamptz)
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                v_old tenancy.memberships;
            BEGIN
                IF infra.current_org() IS NOT NULL THEN
                    RAISE EXCEPTION 'platform administration runs without a tenant' USING ERRCODE = 'insufficient_privilege';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM identity.users u WHERE u.id = p_admin AND u.is_platform_admin AND u.is_active) THEN
                    RAISE EXCEPTION 'only an active platform administrator may remove memberships' USING ERRCODE = 'insufficient_privilege';
                END IF;
                IF nullif(btrim(p_reason), '') IS NULL THEN
                    RAISE EXCEPTION 'a reason is required' USING ERRCODE = 'check_violation';
                END IF;

                DELETE FROM tenancy.memberships x
                 WHERE x.organization_id = p_organization_id AND x.user_id = p_user_id
                RETURNING x.* INTO v_old;
                IF NOT FOUND THEN
                    RETURN false;
                END IF;

                INSERT INTO infra.audit_log (occurred_at, user_id, organization_id, entity_type, entity_id, action, changes)
                VALUES (p_now, p_admin, p_organization_id, 'Membership', v_old.id::text, 'Deleted',
                        jsonb_build_object('Id', v_old.id, 'OrganizationId', v_old.organization_id, 'UserId', v_old.user_id,
                                           'Role', v_old.role, 'CreatedAt', v_old.created_at)),
                       (p_now, p_admin, p_organization_id, 'Membership', p_organization_id::text || ',' || p_user_id::text, 'AdminRemoveMember',
                        jsonb_build_object('Source', 'platform', 'Reason', btrim(p_reason),
                                           'Before', jsonb_build_object('Role', v_old.role), 'After', NULL));
                RETURN true;
            END $$;

            REVOKE ALL ON FUNCTION tenancy.admin_remove_membership(uuid, uuid, uuid, text, timestamptz) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION tenancy.admin_remove_membership(uuid, uuid, uuid, text, timestamptz) TO handynet_app;
            """);

        // ---- Counts for the company administration (no operational data, no tenant session) -------------------------------
        m.Sql("""
            CREATE FUNCTION pos.admin_register_names(p_organization_ids uuid[])
                RETURNS TABLE (organization_id uuid, name text, is_active boolean)
                LANGUAGE plpgsql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            BEGIN
                IF infra.current_org() IS NOT NULL THEN
                    RAISE EXCEPTION 'platform administration runs without a tenant' USING ERRCODE = 'insufficient_privilege';
                END IF;
                RETURN QUERY
                    SELECT r.organization_id, r.name::text, r.is_active
                      FROM pos.registers r
                     WHERE r.organization_id = ANY (p_organization_ids)
                     ORDER BY r.organization_id, r.name;
            END $$;

            CREATE FUNCTION marketplace.admin_offer_counts(p_organization_ids uuid[])
                RETURNS TABLE (organization_id uuid, offer_count bigint, published_count bigint)
                LANGUAGE plpgsql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            BEGIN
                IF infra.current_org() IS NOT NULL THEN
                    RAISE EXCEPTION 'platform administration runs without a tenant' USING ERRCODE = 'insufficient_privilege';
                END IF;
                RETURN QUERY
                    SELECT o.organization_id, count(*), count(*) FILTER (WHERE o.status = 'Published')
                      FROM marketplace.offers o
                     WHERE o.organization_id = ANY (p_organization_ids)
                     GROUP BY o.organization_id;
            END $$;

            REVOKE ALL ON FUNCTION pos.admin_register_names(uuid[]) FROM PUBLIC;
            REVOKE ALL ON FUNCTION marketplace.admin_offer_counts(uuid[]) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION pos.admin_register_names(uuid[]) TO handynet_app;
            GRANT EXECUTE ON FUNCTION marketplace.admin_offer_counts(uuid[]) TO handynet_app;
            """);
    }

    public static void Down(MigrationBuilder m) =>
        m.Sql("""
            DROP FUNCTION IF EXISTS marketplace.admin_offer_counts(uuid[]);
            DROP FUNCTION IF EXISTS pos.admin_register_names(uuid[]);
            DROP FUNCTION IF EXISTS tenancy.admin_remove_membership(uuid, uuid, uuid, text, timestamptz);
            DROP TRIGGER IF EXISTS platform_admin_required ON identity.users;
            DROP FUNCTION IF EXISTS identity.ensure_platform_admin_remains();
            DROP TRIGGER IF EXISTS owner_required ON tenancy.memberships;
            DROP FUNCTION IF EXISTS tenancy.ensure_owner_remains();
            """);
}
