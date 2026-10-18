using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "moderated_at",
                schema: "marketplace",
                table: "offers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "moderated_by",
                schema: "marketplace",
                table: "offers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moderation",
                schema: "marketplace",
                table: "offers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "moderation_reason",
                schema: "marketplace",
                table: "offers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "seller_blocks",
                schema: "marketplace",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seller_blocks", x => x.organization_id);
                    table.CheckConstraint("ck_seller_blocks_reason", "is_blocked OR reason IS NULL");
                });

            migrationBuilder.CreateIndex(
                name: "ix_offers_blocked",
                schema: "marketplace",
                table: "offers",
                column: "organization_id",
                filter: "moderation = 'Blocked'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_offers_moderation",
                schema: "marketplace",
                table: "offers",
                sql: "(moderation = 'None' AND moderation_reason IS NULL) OR (moderation = 'Blocked' AND moderated_at IS NOT NULL AND moderated_by IS NOT NULL)");

            migrationBuilder.Sql(SecurityUp);
            migrationBuilder.Sql(FunctionsUp);
            migrationBuilder.Sql(ViewAndReservationUp);
        }

        /// <summary>
        /// Moderation is platform-owned:
        /// - seller_blocks: the app role may only read (RLS: a seller sees its own row); writes go through moderate_seller;
        /// - the moderation columns on offers can only change inside moderate_offers (a trigger checks a transaction-local
        ///   flag that only the definer functions set), and a blocked offer's seller status is frozen — so a seller can
        ///   never re-publish or unblock, even through an application bug.
        /// </summary>
        private const string SecurityUp = """
            REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON marketplace.seller_blocks FROM handynet_app;
            GRANT SELECT ON marketplace.seller_blocks TO handynet_app;
            ALTER TABLE marketplace.seller_blocks ENABLE ROW LEVEL SECURITY;
            CREATE POLICY seller_reads_own ON marketplace.seller_blocks FOR SELECT
                USING (organization_id = infra.current_org());

            CREATE FUNCTION marketplace.guard_offer_moderation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_setting('marketplace.moderating', true) IS NOT DISTINCT FROM 'on' THEN
                    RETURN NEW;
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.moderation <> 'None' OR NEW.moderation_reason IS NOT NULL
                       OR NEW.moderated_at IS NOT NULL OR NEW.moderated_by IS NOT NULL THEN
                        RAISE EXCEPTION 'offer moderation is set by the platform only' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    RETURN NEW;
                END IF;
                IF (NEW.moderation, NEW.moderation_reason, NEW.moderated_at, NEW.moderated_by)
                   IS DISTINCT FROM (OLD.moderation, OLD.moderation_reason, OLD.moderated_at, OLD.moderated_by) THEN
                    RAISE EXCEPTION 'offer moderation is set by the platform only' USING ERRCODE = 'insufficient_privilege';
                END IF;
                IF OLD.moderation = 'Blocked' AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'the status of a blocked offer cannot change' USING ERRCODE = 'insufficient_privilege';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER trg_offers_moderation BEFORE INSERT OR UPDATE ON marketplace.offers
                FOR EACH ROW EXECUTE FUNCTION marketplace.guard_offer_moderation();
            """;

        /// <summary>
        /// Platform-admin paths. Admin requests carry no tenant (infra.current_org() IS NULL); every admin function refuses
        /// to run for a tenant request, so a seller's or buyer's session can never use them. Writes are audited in
        /// infra.audit_log (who, when, reason) in the same statement.
        /// </summary>
        private const string FunctionsUp = """
            CREATE FUNCTION marketplace.moderate_offers(p_offer_ids uuid[], p_block boolean, p_reason text, p_admin uuid, p_now timestamptz)
                RETURNS TABLE (offer_id uuid, seller_organization_id uuid, changed boolean)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                v_target text := CASE WHEN p_block THEN 'Blocked' ELSE 'None' END;
                v_reason text := CASE WHEN p_block THEN nullif(btrim(p_reason), '') END;
            BEGIN
                IF infra.current_org() IS NOT NULL THEN
                    RAISE EXCEPTION 'platform moderation runs without a tenant' USING ERRCODE = 'insufficient_privilege';
                END IF;
                IF p_block IS NULL OR p_admin IS NULL OR p_now IS NULL THEN
                    RAISE EXCEPTION 'block flag, admin and time are required' USING ERRCODE = 'invalid_parameter_value';
                END IF;

                PERFORM set_config('marketplace.moderating', 'on', true);
                RETURN QUERY
                WITH target AS (
                    SELECT o.id, o.organization_id, o.moderation AS old_moderation, o.moderation_reason AS old_reason
                      FROM marketplace.offers o
                     WHERE o.id = ANY (p_offer_ids)
                       FOR UPDATE
                ), updated AS (
                    UPDATE marketplace.offers o
                       SET moderation = v_target, moderation_reason = v_reason, moderated_at = p_now, moderated_by = p_admin
                      FROM target t
                     WHERE o.id = t.id
                       AND (t.old_moderation <> v_target OR t.old_reason IS DISTINCT FROM v_reason)
                    RETURNING o.id, o.organization_id, t.old_moderation, t.old_reason
                ), audited AS (
                    INSERT INTO infra.audit_log (occurred_at, user_id, organization_id, entity_type, entity_id, action, changes)
                    SELECT p_now, p_admin, u.organization_id, 'Offer', u.id::text,
                           CASE WHEN p_block THEN 'Blocked' ELSE 'Unblocked' END,
                           jsonb_build_object(
                               'Moderation', jsonb_build_object('from', u.old_moderation, 'to', v_target),
                               'ModerationReason', jsonb_build_object('from', u.old_reason, 'to', v_reason),
                               'Reason', nullif(btrim(p_reason), ''),
                               'Source', 'platform')
                      FROM updated u
                    RETURNING 1
                )
                SELECT t.id, t.organization_id, EXISTS (SELECT 1 FROM updated u WHERE u.id = t.id)
                  FROM target t;
                PERFORM set_config('marketplace.moderating', '', true);
            END
            $$;

            CREATE FUNCTION marketplace.moderate_seller(p_organization_id uuid, p_block boolean, p_reason text, p_admin uuid, p_now timestamptz)
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                v_reason text := CASE WHEN p_block THEN nullif(btrim(p_reason), '') END;
                v_old record;
            BEGIN
                IF infra.current_org() IS NOT NULL THEN
                    RAISE EXCEPTION 'platform moderation runs without a tenant' USING ERRCODE = 'insufficient_privilege';
                END IF;
                IF p_block IS NULL OR p_admin IS NULL OR p_now IS NULL THEN
                    RAISE EXCEPTION 'block flag, admin and time are required' USING ERRCODE = 'invalid_parameter_value';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM tenancy.organizations org WHERE org.id = p_organization_id) THEN
                    RETURN NULL; -- unknown organization
                END IF;

                SELECT b.is_blocked, b.reason INTO v_old
                  FROM marketplace.seller_blocks b WHERE b.organization_id = p_organization_id FOR UPDATE;
                IF (FOUND AND v_old.is_blocked = p_block AND v_old.reason IS NOT DISTINCT FROM v_reason)
                   OR (NOT FOUND AND NOT p_block) THEN
                    RETURN false; -- already in that state
                END IF;

                INSERT INTO marketplace.seller_blocks (organization_id, is_blocked, reason, changed_at, changed_by)
                VALUES (p_organization_id, p_block, v_reason, p_now, p_admin)
                ON CONFLICT (organization_id) DO UPDATE
                    SET is_blocked = EXCLUDED.is_blocked, reason = EXCLUDED.reason,
                        changed_at = EXCLUDED.changed_at, changed_by = EXCLUDED.changed_by;

                INSERT INTO infra.audit_log (occurred_at, user_id, organization_id, entity_type, entity_id, action, changes)
                VALUES (p_now, p_admin, p_organization_id, 'SellerBlock', p_organization_id::text,
                        CASE WHEN p_block THEN 'Blocked' ELSE 'Unblocked' END,
                        jsonb_build_object(
                            'IsBlocked', jsonb_build_object('from', COALESCE(v_old.is_blocked, false), 'to', p_block),
                            'BlockReason', jsonb_build_object('from', v_old.reason, 'to', v_reason),
                            'Reason', nullif(btrim(p_reason), ''),
                            'Source', 'platform'));
                RETURN true;
            END
            $$;

            -- Offers across all sellers for the moderation screen. p_search is an escaped ILIKE pattern, p_key the
            -- normalized search key (EAN match); p_offer_ids (optional) limits the result to these offers.
            CREATE FUNCTION marketplace.admin_offers(p_offer_ids uuid[], p_seller uuid, p_brand text, p_category text, p_status text, p_blocked boolean,
                                                     p_search text, p_key text, p_offset integer, p_limit integer)
                RETURNS TABLE (offer_id uuid, seller_organization_id uuid, seller_name text, seller_city text, product_id uuid,
                               brand text, product_name text, category text, ean text, mpn text, supplier_sku text,
                               price_minor bigint, currency text, status text, moderation text, moderation_reason text,
                               moderated_at timestamptz, moderated_by uuid, seller_blocked boolean, seller_block_reason text,
                               updated_at timestamptz, total_count bigint)
                LANGUAGE sql STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT o.id, o.organization_id, org.name::text, org.city::text, p.id,
                       p.brand::text, p.name::text, p.category::text, p.ean::text, p.mpn::text, o.supplier_sku::text,
                       o.price_minor, o.currency::text, o.status::text, o.moderation::text, o.moderation_reason::text,
                       o.moderated_at, o.moderated_by, (b.organization_id IS NOT NULL), b.reason::text,
                       o.updated_at, count(*) OVER ()
                  FROM marketplace.offers o
                  JOIN catalog.products p ON p.id = o.product_id
                  JOIN tenancy.organizations org ON org.id = o.organization_id
                  LEFT JOIN marketplace.seller_blocks b ON b.organization_id = o.organization_id AND b.is_blocked
                 WHERE infra.current_org() IS NULL
                   AND (p_offer_ids IS NULL OR o.id = ANY (p_offer_ids))
                   AND (p_seller IS NULL OR o.organization_id = p_seller)
                   AND (p_brand IS NULL OR lower(p.brand) = lower(p_brand))
                   AND (p_category IS NULL OR lower(p.category) = lower(p_category))
                   AND (p_status IS NULL OR o.status = p_status)
                   AND (p_blocked IS NULL OR p_blocked = (o.moderation = 'Blocked' OR b.organization_id IS NOT NULL))
                   AND (p_search IS NULL
                        OR p.name ILIKE p_search OR p.brand ILIKE p_search OR o.supplier_sku ILIKE p_search
                        OR p.mpn ILIKE p_search OR p.ean = p_key OR org.name ILIKE p_search)
                 ORDER BY org.name, p.brand, p.name, o.id
                OFFSET greatest(p_offset, 0) LIMIT least(greatest(p_limit, 1), 500)
            $$;

            -- Organizations that sell (offers, B2B sales module or a block decision) with offer counts and block state.
            CREATE FUNCTION marketplace.admin_sellers(p_organization_id uuid, p_search text, p_limit integer)
                RETURNS TABLE (organization_id uuid, name text, city text, verification text, offer_count bigint,
                               published_count bigint, blocked_offer_count bigint, is_blocked boolean, block_reason text,
                               block_changed_at timestamptz, block_changed_by uuid)
                LANGUAGE sql STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT org.id, org.name::text, org.city::text, org.status::text,
                       count(o.id) FILTER (WHERE o.status <> 'Archived'),
                       count(o.id) FILTER (WHERE o.status = 'Published'),
                       count(o.id) FILTER (WHERE o.moderation = 'Blocked'),
                       COALESCE(b.is_blocked, false), b.reason::text, b.changed_at, b.changed_by
                  FROM tenancy.organizations org
                  LEFT JOIN marketplace.offers o ON o.organization_id = org.id
                  LEFT JOIN marketplace.seller_blocks b ON b.organization_id = org.id
                 WHERE infra.current_org() IS NULL
                   AND (o.id IS NOT NULL OR b.organization_id IS NOT NULL
                        OR EXISTS (SELECT 1 FROM tenancy.organization_modules m
                                   WHERE m.organization_id = org.id AND m.module = 'B2BSales'))
                   AND (p_organization_id IS NULL OR org.id = p_organization_id)
                   AND (p_search IS NULL OR org.name ILIKE p_search OR org.city ILIKE p_search)
                 GROUP BY org.id, org.name, org.city, org.status, b.is_blocked, b.reason, b.changed_at, b.changed_by
                 ORDER BY org.name, org.id
                 LIMIT least(greatest(p_limit, 1), 1000)
            $$;

            -- Which of these offers the platform blocks (offer or whole seller). Lets ordering answer offer.blocked
            -- instead of a generic "unavailable"; reveals nothing beyond the ids the caller already has.
            CREATE FUNCTION marketplace.blocked_offers(p_offer_ids uuid[])
                RETURNS SETOF uuid
                LANGUAGE sql STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
                SELECT o.id
                  FROM marketplace.offers o
                 WHERE o.id = ANY (p_offer_ids)
                   AND (o.moderation = 'Blocked'
                        OR EXISTS (SELECT 1 FROM marketplace.seller_blocks b WHERE b.organization_id = o.organization_id AND b.is_blocked))
            $$;

            REVOKE ALL ON FUNCTION marketplace.guard_offer_moderation() FROM PUBLIC;
            REVOKE ALL ON FUNCTION marketplace.moderate_offers(uuid[], boolean, text, uuid, timestamptz) FROM PUBLIC;
            REVOKE ALL ON FUNCTION marketplace.moderate_seller(uuid, boolean, text, uuid, timestamptz) FROM PUBLIC;
            REVOKE ALL ON FUNCTION marketplace.admin_offers(uuid[], uuid, text, text, text, boolean, text, text, integer, integer) FROM PUBLIC;
            REVOKE ALL ON FUNCTION marketplace.admin_sellers(uuid, text, integer) FROM PUBLIC;
            REVOKE ALL ON FUNCTION marketplace.blocked_offers(uuid[]) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION marketplace.moderate_offers(uuid[], boolean, text, uuid, timestamptz) TO handynet_app;
            GRANT EXECUTE ON FUNCTION marketplace.moderate_seller(uuid, boolean, text, uuid, timestamptz) TO handynet_app;
            GRANT EXECUTE ON FUNCTION marketplace.admin_offers(uuid[], uuid, text, text, text, boolean, text, text, integer, integer) TO handynet_app;
            GRANT EXECUTE ON FUNCTION marketplace.admin_sellers(uuid, text, integer) TO handynet_app;
            GRANT EXECUTE ON FUNCTION marketplace.blocked_offers(uuid[]) TO handynet_app;
            """;

        /// <summary>
        /// The listing view and the reservation function additionally exclude blocked offers and offers of blocked
        /// sellers, so browse, facets, detail, cart refresh and order placement all stop seeing them at once.
        /// </summary>
        private const string ViewAndReservationUp = """
            CREATE OR REPLACE VIEW marketplace.offer_listings WITH (security_barrier = true) AS
            SELECT o.id                      AS offer_id,
                   o.organization_id         AS seller_organization_id,
                   org.name                  AS seller_name,
                   org.city                  AS seller_city,
                   p.id                      AS product_id,
                   p.brand,
                   p.name                    AS product_name,
                   p.category,
                   p.quality,
                   p.ean,
                   p.mpn,
                   o.supplier_sku,
                   o.price_minor,
                   o.currency,
                   o.min_order_quantity,
                   o.delivery_days,
                   o.condition,
                   GREATEST(COALESCE(s.on_hand - s.reserved, 0), 0) AS available,
                   o.updated_at,
                   p.model_name,
                   p.compatible_models,
                   p.model_keys,
                   img.image_count,
                   img.primary_image_id
            FROM marketplace.offers o
            JOIN catalog.products p ON p.id = o.product_id
            JOIN tenancy.organizations org ON org.id = o.organization_id AND org.status = 'Verified'
            LEFT JOIN inventory.stock_levels s ON s.warehouse_id = o.warehouse_id AND s.product_id = o.product_id
            CROSS JOIN LATERAL (SELECT count(*)::integer AS image_count,
                                       (array_agg(i.id ORDER BY i.position))[1] AS primary_image_id
                                  FROM catalog.product_images i
                                 WHERE i.product_id = p.id AND i.removed_at IS NULL) img
            WHERE o.status = 'Published'
              AND o.moderation = 'None'
              AND infra.current_org() IS NOT NULL
              AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                          WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales')
              AND NOT EXISTS (SELECT 1 FROM marketplace.seller_blocks b
                              WHERE b.organization_id = o.organization_id AND b.is_blocked);

            CREATE OR REPLACE FUNCTION marketplace.reserve_offer_stock(p_offer_id uuid, p_quantity integer, p_reference text)
                RETURNS uuid
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                v_buyer uuid := infra.current_org();
                v_offer record;
                v_reservation uuid;
            BEGIN
                IF v_buyer IS NULL OR p_quantity IS NULL OR p_quantity <= 0 THEN
                    RETURN NULL;
                END IF;

                -- The buyer must be a verified organization with B2B purchasing enabled.
                IF NOT EXISTS (SELECT 1 FROM tenancy.organizations b
                               JOIN tenancy.organization_modules m ON m.organization_id = b.id AND m.module = 'B2BPurchasing'
                               WHERE b.id = v_buyer AND b.status = 'Verified') THEN
                    RETURN NULL;
                END IF;

                -- FOR SHARE: a concurrent platform block (UPDATE of the offer) waits until this checkout commits, or the
                -- checkout sees the block.
                SELECT o.organization_id, o.warehouse_id, o.product_id, o.min_order_quantity
                  INTO v_offer
                  FROM marketplace.offers o
                  JOIN tenancy.organizations s ON s.id = o.organization_id AND s.status = 'Verified'
                 WHERE o.id = p_offer_id
                   AND o.status = 'Published'
                   AND o.moderation = 'None'
                   AND o.organization_id <> v_buyer
                   AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                               WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales')
                   AND NOT EXISTS (SELECT 1 FROM marketplace.seller_blocks sb
                                   WHERE sb.organization_id = o.organization_id AND sb.is_blocked)
                   FOR SHARE OF o;

                IF NOT FOUND OR p_quantity < v_offer.min_order_quantity THEN
                    RETURN NULL;
                END IF;

                UPDATE inventory.stock_levels
                   SET reserved = reserved + p_quantity, updated_at = now()
                 WHERE warehouse_id = v_offer.warehouse_id
                   AND product_id = v_offer.product_id
                   AND on_hand - reserved >= p_quantity;

                IF NOT FOUND THEN
                    RETURN NULL;
                END IF;

                INSERT INTO inventory.stock_reservations
                    (id, organization_id, warehouse_id, product_id, quantity, status, reference, reserved_by_organization_id, created_at)
                VALUES
                    (gen_random_uuid(), v_offer.organization_id, v_offer.warehouse_id, v_offer.product_id, p_quantity,
                     'Active', p_reference, v_buyer, now())
                RETURNING id INTO v_reservation;

                RETURN v_reservation;
            END
            $$;
            """;

        /// <summary>The view as defined in LagerAndMarketplaceUsability and the reservation function as in TenantIsolationAndReservations.</summary>
        private const string ViewAndReservationDown = """
            CREATE OR REPLACE VIEW marketplace.offer_listings WITH (security_barrier = true) AS
            SELECT o.id                      AS offer_id,
                   o.organization_id         AS seller_organization_id,
                   org.name                  AS seller_name,
                   org.city                  AS seller_city,
                   p.id                      AS product_id,
                   p.brand,
                   p.name                    AS product_name,
                   p.category,
                   p.quality,
                   p.ean,
                   p.mpn,
                   o.supplier_sku,
                   o.price_minor,
                   o.currency,
                   o.min_order_quantity,
                   o.delivery_days,
                   o.condition,
                   GREATEST(COALESCE(s.on_hand - s.reserved, 0), 0) AS available,
                   o.updated_at,
                   p.model_name,
                   p.compatible_models,
                   p.model_keys,
                   img.image_count,
                   img.primary_image_id
            FROM marketplace.offers o
            JOIN catalog.products p ON p.id = o.product_id
            JOIN tenancy.organizations org ON org.id = o.organization_id AND org.status = 'Verified'
            LEFT JOIN inventory.stock_levels s ON s.warehouse_id = o.warehouse_id AND s.product_id = o.product_id
            CROSS JOIN LATERAL (SELECT count(*)::integer AS image_count,
                                       (array_agg(i.id ORDER BY i.position))[1] AS primary_image_id
                                  FROM catalog.product_images i
                                 WHERE i.product_id = p.id AND i.removed_at IS NULL) img
            WHERE o.status = 'Published'
              AND infra.current_org() IS NOT NULL
              AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                          WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales');

            CREATE OR REPLACE FUNCTION marketplace.reserve_offer_stock(p_offer_id uuid, p_quantity integer, p_reference text)
                RETURNS uuid
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                v_buyer uuid := infra.current_org();
                v_offer record;
                v_reservation uuid;
            BEGIN
                IF v_buyer IS NULL OR p_quantity IS NULL OR p_quantity <= 0 THEN
                    RETURN NULL;
                END IF;

                IF NOT EXISTS (SELECT 1 FROM tenancy.organizations b
                               JOIN tenancy.organization_modules m ON m.organization_id = b.id AND m.module = 'B2BPurchasing'
                               WHERE b.id = v_buyer AND b.status = 'Verified') THEN
                    RETURN NULL;
                END IF;

                SELECT o.organization_id, o.warehouse_id, o.product_id, o.min_order_quantity
                  INTO v_offer
                  FROM marketplace.offers o
                  JOIN tenancy.organizations s ON s.id = o.organization_id AND s.status = 'Verified'
                 WHERE o.id = p_offer_id
                   AND o.status = 'Published'
                   AND o.organization_id <> v_buyer
                   AND EXISTS (SELECT 1 FROM tenancy.organization_modules m
                               WHERE m.organization_id = o.organization_id AND m.module = 'B2BSales');

                IF NOT FOUND OR p_quantity < v_offer.min_order_quantity THEN
                    RETURN NULL;
                END IF;

                UPDATE inventory.stock_levels
                   SET reserved = reserved + p_quantity, updated_at = now()
                 WHERE warehouse_id = v_offer.warehouse_id
                   AND product_id = v_offer.product_id
                   AND on_hand - reserved >= p_quantity;

                IF NOT FOUND THEN
                    RETURN NULL;
                END IF;

                INSERT INTO inventory.stock_reservations
                    (id, organization_id, warehouse_id, product_id, quantity, status, reference, reserved_by_organization_id, created_at)
                VALUES
                    (gen_random_uuid(), v_offer.organization_id, v_offer.warehouse_id, v_offer.product_id, p_quantity,
                     'Active', p_reference, v_buyer, now())
                RETURNING id INTO v_reservation;

                RETURN v_reservation;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ViewAndReservationDown);
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS marketplace.blocked_offers(uuid[]);
                DROP FUNCTION IF EXISTS marketplace.admin_sellers(uuid, text, integer);
                DROP FUNCTION IF EXISTS marketplace.admin_offers(uuid[], uuid, text, text, text, boolean, text, text, integer, integer);
                DROP FUNCTION IF EXISTS marketplace.moderate_seller(uuid, boolean, text, uuid, timestamptz);
                DROP FUNCTION IF EXISTS marketplace.moderate_offers(uuid[], boolean, text, uuid, timestamptz);
                DROP TRIGGER IF EXISTS trg_offers_moderation ON marketplace.offers;
                DROP FUNCTION IF EXISTS marketplace.guard_offer_moderation();
                DROP POLICY IF EXISTS seller_reads_own ON marketplace.seller_blocks;
                """);

            migrationBuilder.DropTable(
                name: "seller_blocks",
                schema: "marketplace");

            migrationBuilder.DropIndex(
                name: "ix_offers_blocked",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_offers_moderation",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "moderated_at",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "moderated_by",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "moderation",
                schema: "marketplace",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "moderation_reason",
                schema: "marketplace",
                table: "offers");
        }
    }
}
