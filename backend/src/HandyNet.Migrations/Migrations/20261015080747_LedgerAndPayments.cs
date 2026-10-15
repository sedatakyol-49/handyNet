using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HandyNet.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class LedgerAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    checkout_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    commission_basis_points = table.Column<int>(type: "integer", nullable: false),
                    commission_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    commission_vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amounts", "amount_minor > 0 AND commission_net_minor >= 0 AND commission_vat_minor >= 0 AND commission_net_minor + commission_vat_minor <= amount_minor");
                    table.ForeignKey(
                        name: "fk_payments_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "postings",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_postings", x => x.id);
                    table.CheckConstraint("ck_postings_nonzero", "amount_minor <> 0");
                    table.ForeignKey(
                        name: "fk_postings_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_postings_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalSchema: "ledger",
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    provider_transfer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payouts", x => x.id);
                    table.CheckConstraint("ck_payouts_amount", "amount_minor > 0");
                    table.ForeignKey(
                        name: "fk_payouts_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "payments",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_organization_id_type_currency",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "organization_id", "type", "currency" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_idempotency_key",
                schema: "ledger",
                table: "journal_entries",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_source_type_source_id",
                schema: "ledger",
                table: "journal_entries",
                columns: new[] { "source_type", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_provider_provider_payment_id",
                schema: "payments",
                table: "payments",
                columns: new[] { "provider", "provider_payment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payments_one_live_per_order",
                schema: "payments",
                table: "payments",
                column: "order_id",
                unique: true,
                filter: "status IN ('Open', 'Paid')");

            migrationBuilder.CreateIndex(
                name: "ix_payouts_payment_id",
                schema: "payments",
                table: "payouts",
                column: "payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payouts_seller_organization_id_created_at",
                schema: "payments",
                table: "payouts",
                columns: new[] { "seller_organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_postings_account_id",
                schema: "ledger",
                table: "postings",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_postings_journal_entry_id",
                schema: "ledger",
                table: "postings",
                column: "journal_entry_id");

            // ---- Ledger: read-only for the app; the only write path is ledger.post_entry ---------------------
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA ledger TO handynet_app;
                REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ledger FROM handynet_app;
                GRANT SELECT ON ALL TABLES IN SCHEMA ledger TO handynet_app;

                -- Immutable even for the owner role: corrections are reversing entries, never edits.
                CREATE FUNCTION ledger.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ledger rows are immutable (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = '42501';
                END $$;
                CREATE TRIGGER journal_entries_immutable BEFORE UPDATE OR DELETE ON ledger.journal_entries
                    FOR EACH ROW EXECUTE FUNCTION ledger.forbid_change();
                CREATE TRIGGER postings_immutable BEFORE UPDATE OR DELETE ON ledger.postings
                    FOR EACH ROW EXECUTE FUNCTION ledger.forbid_change();

                ALTER TABLE ledger.accounts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY own_accounts ON ledger.accounts FOR SELECT USING (organization_id = infra.current_org());
                ALTER TABLE ledger.postings ENABLE ROW LEVEL SECURITY;
                CREATE POLICY own_postings ON ledger.postings FOR SELECT
                    USING (EXISTS (SELECT 1 FROM ledger.accounts a WHERE a.id = account_id));
                ALTER TABLE ledger.journal_entries ENABLE ROW LEVEL SECURITY;
                CREATE POLICY own_entries ON ledger.journal_entries FOR SELECT
                    USING (EXISTS (SELECT 1 FROM ledger.postings p WHERE p.journal_entry_id = journal_entries.id));

                CREATE FUNCTION ledger.post_entry(
                    p_idempotency_key text, p_description text, p_source_type text, p_source_id uuid,
                    p_occurred_at timestamptz, p_lines jsonb)
                    RETURNS uuid
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_entry uuid;
                    v_line jsonb;
                    v_account uuid;
                    v_org uuid;
                    v_type text;
                    v_currency text;
                    v_amount bigint;
                    v_tenant uuid := infra.current_org();
                BEGIN
                    SELECT id INTO v_entry FROM ledger.journal_entries WHERE idempotency_key = p_idempotency_key;
                    IF FOUND THEN
                        RETURN v_entry; -- already posted (at-least-once callers)
                    END IF;

                    IF jsonb_typeof(p_lines) <> 'array' OR jsonb_array_length(p_lines) < 2 THEN
                        RAISE EXCEPTION 'ledger: at least two postings required' USING ERRCODE = '22023';
                    END IF;
                    IF EXISTS (SELECT 1 FROM (
                                   SELECT l->>'currency' AS currency, sum((l->>'amount_minor')::bigint) AS total
                                   FROM jsonb_array_elements(p_lines) l GROUP BY 1) s
                               WHERE s.total <> 0) THEN
                        RAISE EXCEPTION 'ledger: entry % is not balanced', p_idempotency_key USING ERRCODE = '22023';
                    END IF;

                    v_entry := gen_random_uuid();
                    INSERT INTO ledger.journal_entries (id, idempotency_key, description, source_type, source_id, occurred_at, recorded_at)
                    VALUES (v_entry, p_idempotency_key, p_description, p_source_type, p_source_id, p_occurred_at, now());

                    FOR v_line IN SELECT * FROM jsonb_array_elements(p_lines) LOOP
                        v_type := v_line->>'account_type';
                        v_org := NULLIF(v_line->>'organization_id', '')::uuid;
                        v_currency := v_line->>'currency';
                        v_amount := (v_line->>'amount_minor')::bigint;

                        IF v_amount = 0 THEN
                            RAISE EXCEPTION 'ledger: zero posting' USING ERRCODE = '22023';
                        END IF;
                        IF v_type NOT IN ('PspClearing', 'SellerPayable', 'CommissionRevenue', 'VatPayable') THEN
                            RAISE EXCEPTION 'ledger: unknown account type %', v_type USING ERRCODE = '22023';
                        END IF;
                        IF (v_type = 'SellerPayable') <> (v_org IS NOT NULL) THEN
                            RAISE EXCEPTION 'ledger: account owner mismatch for %', v_type USING ERRCODE = '22023';
                        END IF;
                        -- A tenant can only move money on its own seller account.
                        IF v_org IS NOT NULL AND v_org IS DISTINCT FROM v_tenant THEN
                            RAISE EXCEPTION 'ledger: posting to another organization''s account' USING ERRCODE = '42501';
                        END IF;

                        INSERT INTO ledger.accounts (id, organization_id, type, currency, created_at)
                        VALUES (gen_random_uuid(), v_org, v_type, v_currency, now())
                        ON CONFLICT (organization_id, type, currency) DO NOTHING;
                        SELECT id INTO v_account FROM ledger.accounts
                        WHERE organization_id IS NOT DISTINCT FROM v_org AND type = v_type AND currency = v_currency;

                        INSERT INTO ledger.postings (journal_entry_id, account_id, amount_minor, currency)
                        VALUES (v_entry, v_account, v_amount, v_currency);
                    END LOOP;

                    RETURN v_entry;
                END
                $$;
                REVOKE ALL ON FUNCTION ledger.post_entry(text, text, text, uuid, timestamptz, jsonb) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION ledger.post_entry(text, text, text, uuid, timestamptz, jsonb) TO handynet_app;
                """);

            // ---- Payments ------------------------------------------------------------------------------
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA payments TO handynet_app;
                GRANT SELECT, INSERT, UPDATE ON payments.payments TO handynet_app;
                GRANT SELECT, INSERT ON payments.payouts TO handynet_app;

                ALTER TABLE payments.payments ENABLE ROW LEVEL SECURITY;
                CREATE POLICY payment_parties ON payments.payments FOR SELECT
                    USING (buyer_organization_id = infra.current_org() OR seller_organization_id = infra.current_org());
                CREATE POLICY buyer_starts ON payments.payments FOR INSERT
                    WITH CHECK (buyer_organization_id = infra.current_org());
                CREATE POLICY seller_processes ON payments.payments FOR UPDATE
                    USING (seller_organization_id = infra.current_org()) WITH CHECK (seller_organization_id = infra.current_org());

                ALTER TABLE payments.payouts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY seller_payouts ON payments.payouts
                    USING (seller_organization_id = infra.current_org()) WITH CHECK (seller_organization_id = infra.current_org());

                CREATE FUNCTION payments.payment_owner(p_provider text, p_provider_payment_id text)
                    RETURNS TABLE (id uuid, seller_organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT p.id, p.seller_organization_id FROM payments.payments p
                    WHERE p.provider = p_provider AND p.provider_payment_id = p_provider_payment_id
                $$;

                CREATE FUNCTION payments.payments_for_order(p_order_id uuid)
                    RETURNS TABLE (id uuid, seller_organization_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT p.id, p.seller_organization_id FROM payments.payments p WHERE p.order_id = p_order_id
                $$;

                REVOKE ALL ON FUNCTION payments.payment_owner(text, text) FROM PUBLIC;
                REVOKE ALL ON FUNCTION payments.payments_for_order(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION payments.payment_owner(text, text) TO handynet_app;
                GRANT EXECUTE ON FUNCTION payments.payments_for_order(uuid) TO handynet_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS payments.payments_for_order(uuid);
                DROP FUNCTION IF EXISTS payments.payment_owner(text, text);
                DROP FUNCTION IF EXISTS ledger.post_entry(text, text, text, uuid, timestamptz, jsonb);
                DROP TRIGGER IF EXISTS postings_immutable ON ledger.postings;
                DROP TRIGGER IF EXISTS journal_entries_immutable ON ledger.journal_entries;
                DROP FUNCTION IF EXISTS ledger.forbid_change();
                """);
            migrationBuilder.DropTable(
                name: "payouts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "postings",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_entries",
                schema: "ledger");
        }
    }
}
