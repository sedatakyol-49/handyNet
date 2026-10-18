using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Platform moderation of marketplace offers (real PostgreSQL, RLS on): blocked offers and offers of blocked sellers
/// disappear from browse, facets, detail and ordering; sellers cannot lift or work around a block; every action is audited.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class MarketplaceModerationTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Key() => new() { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") };

    private static string Str(JsonNode? node) => node!.GetValue<string>();

    private static async Task<int> BrowseCountAsync(ApiClient shop, string ean) =>
        (await shop.GetAsync($"/api/marketplace/offers?search={ean}")).Expect(HttpStatusCode.OK).Json["totalCount"]!.GetValue<int>();

    private static async Task<int> FacetTotalAsync(ApiClient shop, string ean) =>
        (await shop.GetAsync($"/api/marketplace/facets?search={ean}")).Expect(HttpStatusCode.OK).Json["total"]!.GetValue<int>();

    private static Task<ApiResponse> OrderAsync(ApiClient shop, Guid offerId, int quantity = 1) =>
        shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity } } }, Key());

    /// <summary>Imports a published offer without checking the marketplace (it may be hidden by a seller block).</summary>
    private static async Task<(string Sku, string Ean)> ImportOfferAsync(ApiClient supplier, int quantity = 5)
    {
        var ean = Scenario.NewEan();
        var sku = "MOD-" + Scenario.UniqueSuffix();
        var csv = $"Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand\n{sku};{ean};Apple;iPhone 14 Akku;Akku;Original;19,90;{quantity}\n";
        var preview = (await supplier.UploadImportAsync("preise.csv", csv)).Expect(HttpStatusCode.Created);
        (await supplier.PostAsync($"/api/imports/{preview.Json["id"]}/commit")).Expect(HttpStatusCode.OK);
        return (sku, ean);
    }

    private static async Task<JsonNode> OwnOfferAsync(ApiClient supplier, Guid offerId)
    {
        var list = (await supplier.GetAsync("/api/offers?pageSize=200")).Expect(HttpStatusCode.OK);
        return list.Json["items"]!.AsArray().Single(o => Str(o!["id"]) == offerId.ToString())!;
    }

    private static async Task<JsonNode> AdminOfferAsync(ApiClient admin, Guid sellerId, Guid offerId)
    {
        var list = (await admin.GetAsync($"/api/admin/marketplace/offers?sellerId={sellerId}&pageSize=200")).Expect(HttpStatusCode.OK);
        return list.Json["items"]!.AsArray().Single(o => Str(o!["offerId"]) == offerId.ToString())!;
    }

    private async Task<NpgsqlConnection> ConnectAsAppAsync(Guid? organizationId)
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection);
        set.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        await set.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task<long> OwnerCountAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    // ---- single offer -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Blocked_offer_disappears_from_browse_facets_detail_and_ordering_and_unblock_restores_it()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, ean) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5);
        var admin = await _scenario.AdminAsync();
        Assert.Equal(1, await BrowseCountAsync(shop, ean));
        Assert.Equal(1, await FacetTotalAsync(shop, ean));

        var blocked = (await admin.PostAsync($"/api/admin/marketplace/offers/{offerId}/block", new { reason = "Markenrechtsverletzung" }))
            .Expect(HttpStatusCode.OK);
        Assert.True(blocked.Json["blocked"]!.GetValue<bool>());
        Assert.Equal("offer", Str(blocked.Json["blockedBy"]));
        Assert.Equal("Blocked", Str(blocked.Json["moderation"]));
        Assert.Equal("Markenrechtsverletzung", Str(blocked.Json["moderationReason"]));
        Assert.Equal("Published", Str(blocked.Json["status"])); // the seller's own status is untouched

        Assert.Equal(0, await BrowseCountAsync(shop, ean));
        Assert.Equal(0, await FacetTotalAsync(shop, ean));
        (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Expect(HttpStatusCode.NotFound); // cart refresh
        Assert.Equal("offer.not_found", (await shop.GetAsync($"/api/marketplace/offers/{offerId}/detail")).Expect(HttpStatusCode.NotFound).Code);

        var order = await OrderAsync(shop, offerId);
        Assert.Equal(HttpStatusCode.Conflict, order.Status);
        Assert.Equal("offer.blocked", order.Code);
        Assert.Equal(offerId.ToString(), Str(order.Json["offerId"]));

        // Blocking again is a no-op (still 200, still blocked).
        (await admin.PostAsync($"/api/admin/marketplace/offers/{offerId}/block", new { reason = "Markenrechtsverletzung" })).Expect(HttpStatusCode.OK);

        var unblocked = (await admin.PostAsync($"/api/admin/marketplace/offers/{offerId}/unblock", new { reason = "Lizenz nachgewiesen" }))
            .Expect(HttpStatusCode.OK);
        Assert.False(unblocked.Json["blocked"]!.GetValue<bool>());
        Assert.Null(unblocked.Json["moderationReason"]);
        Assert.Equal(1, await BrowseCountAsync(shop, ean));
        Assert.Equal(1, await FacetTotalAsync(shop, ean));
        (await shop.GetAsync($"/api/marketplace/offers/{offerId}/detail")).Expect(HttpStatusCode.OK);
        (await OrderAsync(shop, offerId)).Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Blocked_offer_is_excluded_from_other_offers_on_a_product_detail()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var other = await _scenario.RegisterAsync("grosshandel-b", wholesaler: true);
        await _scenario.ApproveAsync(other, "ProductImport", "Inventory", "B2BSales");
        var (offerA, ean) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 3);
        // Second seller offers the same EAN (same canonical product).
        var csv = $"Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand\nB-{Scenario.UniqueSuffix()};{ean};Apple;iPhone 13 Display;Display;ServicePack;79,90;4\n";
        var preview = (await other.UploadImportAsync("b.csv", csv)).Expect(HttpStatusCode.Created);
        (await other.PostAsync($"/api/imports/{preview.Json["id"]}/commit")).Expect(HttpStatusCode.OK);
        var offerB = Guid.Parse(Str((await shop.GetAsync($"/api/marketplace/offers?search={ean}&sellerId={other.OrganizationId}"))
            .Expect(HttpStatusCode.OK).Json["items"]![0]!["offerId"]));

        var before = (await shop.GetAsync($"/api/marketplace/offers/{offerA}/detail")).Expect(HttpStatusCode.OK);
        Assert.Contains(before.Json["otherOffers"]!.AsArray(), o => Str(o!["offerId"]) == offerB.ToString());

        var admin = await _scenario.AdminAsync();
        (await admin.PostAsync($"/api/admin/marketplace/offers/{offerB}/block", new { reason = "Fälschung" })).Expect(HttpStatusCode.OK);

        var after = (await shop.GetAsync($"/api/marketplace/offers/{offerA}/detail")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(after.Json["otherOffers"]!.AsArray(), o => Str(o!["offerId"]) == offerB.ToString());
        Assert.Equal(1, await BrowseCountAsync(shop, ean));
    }

    [Fact]
    public async Task Seller_sees_the_block_and_cannot_publish_or_unblock()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, ean) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 2);
        var admin = await _scenario.AdminAsync();
        (await admin.PostAsync($"/api/admin/marketplace/offers/{offerId}/block", new { reason = "Falsche Kategorie" })).Expect(HttpStatusCode.OK);

        var own = await OwnOfferAsync(supplier, offerId);
        Assert.True(own["moderation"]!["blocked"]!.GetValue<bool>());
        Assert.Equal("offer", Str(own["moderation"]!["blockedBy"]));
        Assert.Equal("Falsche Kategorie", Str(own["moderation"]!["reason"]));

        // Pausing/archiving (any status change) is refused while blocked …
        foreach (var status in new[] { "Paused", "Archived" })
        {
            var change = await supplier.PutAsync($"/api/offers/{offerId}",
                new { price = 89.90m, minOrderQuantity = 1, deliveryDays = 2, condition = "New", status });
            Assert.Equal(HttpStatusCode.Conflict, change.Status);
            Assert.Equal("offer.blocked", change.Code);
        }
        // … a price change is allowed but does not bring the offer back.
        (await supplier.PutAsync($"/api/offers/{offerId}", new { price = 79.90m, minOrderQuantity = 1, deliveryDays = 2, condition = "New", status = "Published" }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal(0, await BrowseCountAsync(shop, ean));

        Assert.Equal("Blocked", Str((await AdminOfferAsync(admin, supplier.OrganizationId!.Value, offerId))["moderation"]));

        // Sellers (tenants) cannot call the admin API …
        var attempt = await supplier.PostAsync($"/api/admin/marketplace/offers/{offerId}/unblock", new { });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync("/api/admin/marketplace/offers")).Status);

        // … and even with direct SQL as the application role the database refuses.
        await using var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId);
        foreach (var sql in new[]
                 {
                     $"UPDATE marketplace.offers SET moderation = 'None', moderation_reason = NULL WHERE id = '{offerId}'",
                     $"UPDATE marketplace.offers SET status = 'Paused' WHERE id = '{offerId}'",
                     $"SELECT * FROM marketplace.moderate_offers(ARRAY['{offerId}'::uuid], false, NULL, gen_random_uuid(), now())",
                     $"SELECT marketplace.moderate_seller('{supplier.OrganizationId}', false, NULL, gen_random_uuid(), now())",
                 })
        {
            await using var command = new NpgsqlCommand(sql, asSupplier);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }
        Assert.Equal("Blocked", Str((await AdminOfferAsync(admin, supplier.OrganizationId!.Value, offerId))["moderation"]));
    }

    [Fact]
    public async Task New_offers_cannot_be_inserted_as_moderated_by_the_seller()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);
        await using var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId);
        // Copy an own offer row with a forged moderation decision.
        await using var command = new NpgsqlCommand($"""
            INSERT INTO marketplace.offers (id, organization_id, product_id, warehouse_id, supplier_sku, sku_key, price_minor, currency,
                min_order_quantity, delivery_days, condition, status, moderation, moderated_at, moderated_by, created_at, updated_at)
            SELECT gen_random_uuid(), organization_id, product_id, warehouse_id, 'X' || supplier_sku, 'X' || sku_key, price_minor, currency,
                min_order_quantity, delivery_days, condition, status, 'Blocked', now(), gen_random_uuid(), now(), now()
            FROM marketplace.offers WHERE id = '{offerId}'
            """, asSupplier);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    // ---- seller-level block -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Seller_block_hides_existing_and_new_offers_until_lifted()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, ean) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 4);
        var admin = await _scenario.AdminAsync();
        var sellerId = supplier.OrganizationId!.Value;

        var result = (await admin.PostAsync($"/api/admin/marketplace/sellers/{sellerId}/block", new { reason = "Zahlungsverzug" }))
            .Expect(HttpStatusCode.OK);
        Assert.True(result.Json["changed"]!.GetValue<bool>());
        Assert.True(result.Json["seller"]!["blocked"]!.GetValue<bool>());
        Assert.Equal("Zahlungsverzug", Str(result.Json["seller"]!["blockReason"]));

        Assert.Equal(0, await BrowseCountAsync(shop, ean));
        Assert.Equal(0, await FacetTotalAsync(shop, ean));
        var facets = (await shop.GetAsync("/api/marketplace/facets")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(facets.Json["sellers"]!.AsArray(), s => Str(s!["id"]) == sellerId.ToString());
        Assert.Equal("offer.blocked", (await OrderAsync(shop, offerId)).Code);

        // An offer created while the block lasts is hidden as well.
        var (_, newEan) = await ImportOfferAsync(supplier);
        Assert.Equal(0, await BrowseCountAsync(shop, newEan));

        // The seller sees why.
        var moderation = (await supplier.GetAsync("/api/offers/moderation")).Expect(HttpStatusCode.OK);
        Assert.True(moderation.Json["sellerBlocked"]!.GetValue<bool>());
        Assert.Equal("Zahlungsverzug", Str(moderation.Json["reason"]));
        var own = await OwnOfferAsync(supplier, offerId);
        Assert.Equal("seller", Str(own["moderation"]!["blockedBy"]));

        // Admin views: every offer of the seller counts as blocked (by the seller block); the seller list shows the state.
        var adminOffers = (await admin.GetAsync($"/api/admin/marketplace/offers?sellerId={sellerId}")).Expect(HttpStatusCode.OK);
        Assert.Equal(2, adminOffers.Json["totalCount"]!.GetValue<int>());
        Assert.All(adminOffers.Json["items"]!.AsArray(), o =>
        {
            Assert.True(o!["blocked"]!.GetValue<bool>());
            Assert.Equal("seller", Str(o["blockedBy"]));
        });
        var sellers = (await admin.GetAsync($"/api/admin/marketplace/sellers?search={Uri.EscapeDataString(Str((await supplier.GetAsync("/api/me")).Json["memberships"]![0]!["organizationName"]))}"))
            .Expect(HttpStatusCode.OK);
        var seller = sellers.Json.AsArray().Single(s => Str(s!["organizationId"]) == sellerId.ToString())!;
        Assert.True(seller["blocked"]!.GetValue<bool>());
        Assert.Equal(2, seller["offerCount"]!.GetValue<int>());

        // Also block one offer individually, then lift the seller block: only the individually blocked offer stays hidden.
        (await admin.PostAsync($"/api/admin/marketplace/offers/{offerId}/block", new { reason = "Einzeln gesperrt" })).Expect(HttpStatusCode.OK);
        var lifted = (await admin.PostAsync($"/api/admin/marketplace/sellers/{sellerId}/unblock", new { reason = "Bezahlt" })).Expect(HttpStatusCode.OK);
        Assert.False(lifted.Json["seller"]!["blocked"]!.GetValue<bool>());
        Assert.Equal(1, lifted.Json["seller"]!["blockedOfferCount"]!.GetValue<int>());

        Assert.Equal(1, await BrowseCountAsync(shop, newEan));
        Assert.Equal(0, await BrowseCountAsync(shop, ean));
        Assert.False((await supplier.GetAsync("/api/offers/moderation")).Json["sellerBlocked"]!.GetValue<bool>());

        // Lifting again changes nothing.
        Assert.False((await admin.PostAsync($"/api/admin/marketplace/sellers/{sellerId}/unblock", new { })).Expect(HttpStatusCode.OK)
            .Json["changed"]!.GetValue<bool>());
        Assert.Equal("organization.not_found",
            (await admin.PostAsync($"/api/admin/marketplace/sellers/{Guid.NewGuid()}/block", new { })).Expect(HttpStatusCode.NotFound).Code);
    }

    // ---- bulk, audit, filters -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_block_is_all_or_nothing_idempotent_and_audited()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (a, eanA) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 2);
        var (b, eanB) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 2);
        var admin = await _scenario.AdminAsync();
        var adminId = Str((await admin.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json["userId"]);

        // One unknown id: nothing changes.
        var unknown = await admin.PostAsync("/api/admin/marketplace/offers/block", new { offerIds = new[] { a, b, Guid.NewGuid() }, reason = "Sammelsperre" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("moderation.offer_not_found", unknown.Code);
        Assert.Equal(1, await BrowseCountAsync(shop, eanA));

        var bulk = (await admin.PostAsync("/api/admin/marketplace/offers/block", new { offerIds = new[] { a, b }, reason = "Sammelsperre" }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal(2, bulk.Json["requested"]!.GetValue<int>());
        Assert.Equal(2, bulk.Json["changed"]!.GetValue<int>());
        Assert.All(bulk.Json["offers"]!.AsArray(), o => Assert.True(o!["blocked"]!.GetValue<bool>()));
        Assert.Equal(adminId, Str(bulk.Json["offers"]![0]!["moderatedBy"]));
        Assert.Equal(0, await BrowseCountAsync(shop, eanA));
        Assert.Equal(0, await BrowseCountAsync(shop, eanB));

        var again = (await admin.PostAsync("/api/admin/marketplace/offers/block", new { offerIds = new[] { a, b }, reason = "Sammelsperre" }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal(0, again.Json["changed"]!.GetValue<int>());

        Assert.Equal("validation.failed",
            (await admin.PostAsync("/api/admin/marketplace/offers/block", new { offerIds = Array.Empty<Guid>() })).Expect(HttpStatusCode.UnprocessableEntity).Code);
        Assert.Equal("validation.failed",
            (await admin.PostAsync($"/api/admin/marketplace/offers/{a}/block", new { reason = new string('x', 501) })).Expect(HttpStatusCode.UnprocessableEntity).Code);
        Assert.Equal("offer.not_found",
            (await admin.PostAsync($"/api/admin/marketplace/offers/{Guid.NewGuid()}/block", new { })).Expect(HttpStatusCode.NotFound).Code);

        (await admin.PostAsync("/api/admin/marketplace/offers/unblock", new { offerIds = new[] { a, b }, reason = "Geprüft" })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, await BrowseCountAsync(shop, eanA));
        Assert.Equal(1, await BrowseCountAsync(shop, eanB));

        // Audit: exactly one Blocked and one Unblocked entry per offer, with admin, seller and reason.
        foreach (var id in new[] { a, b })
        {
            Assert.Equal(1, await OwnerCountAsync($"""
                SELECT count(*) FROM infra.audit_log
                 WHERE entity_type = 'Offer' AND entity_id = '{id}' AND action = 'Blocked' AND user_id = '{adminId}'
                   AND organization_id = '{supplier.OrganizationId}' AND changes->>'Reason' = 'Sammelsperre'
                   AND changes->'Moderation'->>'to' = 'Blocked'
                """));
            Assert.Equal(1, await OwnerCountAsync($"""
                SELECT count(*) FROM infra.audit_log
                 WHERE entity_type = 'Offer' AND entity_id = '{id}' AND action = 'Unblocked' AND user_id = '{adminId}'
                   AND changes->>'Reason' = 'Geprüft'
                """));
        }

        (await admin.PostAsync($"/api/admin/marketplace/sellers/{supplier.OrganizationId}/block", new { reason = "Prüfung" })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, await OwnerCountAsync($"""
            SELECT count(*) FROM infra.audit_log
             WHERE entity_type = 'SellerBlock' AND entity_id = '{supplier.OrganizationId}' AND action = 'Blocked'
               AND user_id = '{adminId}' AND changes->>'Reason' = 'Prüfung'
            """));
    }

    [Fact]
    public async Task Admin_offer_list_filters_by_seller_blocked_status_and_text()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (a, eanA) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);
        var (b, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);
        var admin = await _scenario.AdminAsync();
        var sellerId = supplier.OrganizationId!.Value;
        (await admin.PostAsync($"/api/admin/marketplace/offers/{a}/block", new { reason = "Test" })).Expect(HttpStatusCode.OK);

        async Task<JsonArray> Items(string query) =>
            (await admin.GetAsync($"/api/admin/marketplace/offers?sellerId={sellerId}&{query}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();

        Assert.Equal(new[] { a.ToString() }, (await Items("blocked=true")).Select(o => Str(o!["offerId"])));
        Assert.Equal(new[] { b.ToString() }, (await Items("blocked=false")).Select(o => Str(o!["offerId"])));
        Assert.Equal(new[] { a.ToString() }, (await Items($"search={eanA}")).Select(o => Str(o!["offerId"])));
        Assert.Equal(2, (await Items("status=Published&brand=apple&category=DISPLAY")).Count);
        Assert.Empty(await Items("status=Paused"));

        var paged = (await admin.GetAsync($"/api/admin/marketplace/offers?sellerId={sellerId}&pageSize=1&page=2")).Expect(HttpStatusCode.OK);
        Assert.Equal(2, paged.Json["totalCount"]!.GetValue<int>());
        Assert.Single(paged.Json["items"]!.AsArray());
        var beyond = (await admin.GetAsync($"/api/admin/marketplace/offers?sellerId={sellerId}&pageSize=1&page=9")).Expect(HttpStatusCode.OK);
        Assert.Equal(2, beyond.Json["totalCount"]!.GetValue<int>());

        Assert.Equal("validation.failed", (await admin.GetAsync("/api/admin/marketplace/offers?status=Nonsense")).Expect(HttpStatusCode.UnprocessableEntity).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.CreateClient().GetAsync("/api/admin/marketplace/sellers")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/admin/marketplace/sellers")).Status);
    }

    // ---- isolation ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Moderation_data_keeps_tenant_isolation()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);
        var admin = await _scenario.AdminAsync();
        (await admin.PostAsync($"/api/admin/marketplace/sellers/{supplier.OrganizationId}/block", new { reason = "Intern" })).Expect(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/admin/marketplace/offers/{offerId}/block", new { reason = "Intern" })).Expect(HttpStatusCode.OK);

        static async Task<long> Count(NpgsqlConnection c, string sql)
        {
            await using var command = new NpgsqlCommand(sql, c);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        await using (var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId))
        {
            Assert.Equal(1, await Count(asSupplier, "SELECT count(*) FROM marketplace.seller_blocks"));
            // Admin read functions return nothing for a tenant session.
            Assert.Equal(0, await Count(asSupplier, "SELECT count(*) FROM marketplace.admin_offers(NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, 50)"));
            Assert.Equal(0, await Count(asSupplier, "SELECT count(*) FROM marketplace.admin_sellers(NULL, NULL, 50)"));
            await using var insert = new NpgsqlCommand(
                $"UPDATE marketplace.seller_blocks SET is_blocked = false, reason = NULL WHERE organization_id = '{supplier.OrganizationId}'", asSupplier);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var asShop = await ConnectAsAppAsync(shop.OrganizationId))
        {
            // The shop sees neither the supplier's block row nor its offer rows.
            Assert.Equal(0, await Count(asShop, $"SELECT count(*) FROM marketplace.seller_blocks WHERE organization_id = '{supplier.OrganizationId}'"));
            Assert.Equal(0, await Count(asShop, $"SELECT count(*) FROM marketplace.offers WHERE organization_id = '{supplier.OrganizationId}'"));
            // It can learn only that an offer id it already has is blocked.
            Assert.Equal(1, await Count(asShop, $"SELECT count(*) FROM marketplace.blocked_offers(ARRAY['{offerId}'::uuid])"));
            // The reservation function refuses a blocked offer even if a client skips the listing check (race with a block).
            Assert.Equal(1, await Count(asShop, $"SELECT count(*) WHERE marketplace.reserve_offer_stock('{offerId}', 1, 'test') IS NULL"));
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO marketplace.seller_blocks (organization_id, is_blocked, changed_at, changed_by) VALUES ('{shop.OrganizationId}', false, now(), gen_random_uuid())",
                asShop);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var noTenant = await ConnectAsAppAsync(null))
        {
            Assert.Equal(0, await Count(noTenant, "SELECT count(*) FROM marketplace.seller_blocks"));
            Assert.Equal(0, await Count(noTenant, "SELECT count(*) FROM marketplace.offer_listings"));
        }

        // Another seller's offers are not affected by this seller's block.
        var other = await _scenario.RegisterAsync("grosshandel-c", wholesaler: true);
        await _scenario.ApproveAsync(other, "ProductImport", "Inventory", "B2BSales");
        var (otherOffer, otherEan) = await _scenario.PublishOfferAsync(other, shop, quantity: 1);
        Assert.Equal(1, await BrowseCountAsync(shop, otherEan));
        (await OrderAsync(shop, otherOffer)).Expect(HttpStatusCode.Created);
    }
}
