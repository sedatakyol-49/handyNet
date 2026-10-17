using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>Phase 2b: manual Lager entry, product images, marketplace detail, filters and facets (real PostgreSQL, RLS on).</summary>
[Collection(HandyNetCollection.Name)]
public sealed class LagerMarketplaceTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static Task<ApiResponse> EntryAsync(ApiClient client, object body, string? key) =>
        client.PostAsync("/api/inventory/items", body, key is null ? null : new Dictionary<string, string> { ["Idempotency-Key"] = key });

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>A unique IMEI with a valid Luhn check digit.</summary>
    private static string NewImei()
    {
        var body = "35" + Random.Shared.NextInt64(0, 1_000_000_000_000).ToString("D12");
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var d = body[i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return body + (10 - sum % 10) % 10;
    }

    private static object NewPart(string ean, string brand = "Apple", string quality = "ServicePack", string category = "Display",
        string[]? compatible = null) => new
    {
        brand,
        name = $"{brand} Display {ean[^4..]}",
        category,
        quality,
        ean,
        modelName = (string?)null,
        compatibleModels = compatible ?? ["iPhone 13"],
    };

    private static async Task<ApiResponse> UploadAsync(ApiClient client, string path, byte[] data, string contentType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "foto.jpg");
        return await client.SendAsync(HttpMethod.Post, path, content: form);
    }

    private async Task<(ApiClient Supplier, ApiClient Shop)> PartiesAsync()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        return (supplier, shop);
    }

    private async Task SetModulesAsync(ApiClient organization, params string[] modules)
    {
        var admin = await _scenario.AdminAsync();
        (await admin.PutAsync($"/api/admin/organizations/{organization.OrganizationId}/modules", new { modules })).Expect(HttpStatusCode.OK);
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

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static int Int(JsonNode? node) => node!.GetValue<int>();
    private static string Str(JsonNode? node) => node!.GetValue<string>();

    // ---- manual entry ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Manual_entry_creates_product_stock_and_published_offer_and_is_idempotent()
    {
        var (supplier, shop) = await PartiesAsync();
        var ean = Scenario.NewEan();
        var sku = "MAN-" + Scenario.UniqueSuffix();
        var body = new
        {
            newProduct = NewPart(ean),
            quantity = 5,
            purchasePrice = 12.50m,
            offer = new { supplierSku = sku, price = 49.90m, minOrderQuantity = 2, deliveryDays = 3, condition = "New", publish = true },
        };
        var key = Key();

        var created = (await EntryAsync(supplier, body, key)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Created", Str(created["productAction"]));
        Assert.Equal(ean, Str(created["product"]!["ean"]));
        Assert.Equal(5, Int(created["quantityReceived"]));
        Assert.Equal(1250, created["unitCostMinor"]!.GetValue<long>());
        Assert.Equal(5, Int(created["stock"]!["onHand"]));
        Assert.Equal("Created", Str(created["offer"]!["action"]));
        Assert.Equal("Published", Str(created["offer"]!["status"]));
        Assert.Equal(4990, created["offer"]!["priceMinor"]!.GetValue<long>());

        // Replay: same key + body → original result, nothing applied twice.
        var replay = (await EntryAsync(supplier, body, key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Str(created["entryId"]), Str(replay["entryId"]));
        var productId = Str(created["product"]!["id"]);
        var stock = (await supplier.GetAsync("/api/inventory/stock")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Single(i => Str(i!["productId"]) == productId)!;
        Assert.Equal(5, Int(stock["onHand"]));

        // Same key, different body → refused.
        var reused = await EntryAsync(supplier, new { productId, quantity = 1 }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.Status);
        Assert.Equal("entry.idempotency_key_reused", reused.Code);

        // No key → refused.
        Assert.Equal("entry.idempotency_key", (await EntryAsync(supplier, body, null)).Code);

        // The offer is on the marketplace; the receipt and its EK are in the movement history.
        var listing = (await shop.GetAsync($"/api/marketplace/offers?search={sku}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single()!;
        Assert.Equal(2, Int(listing["minOrderQuantity"]));
        Assert.Equal("iPhone 13", Str(listing["compatibleModels"]![0]));
        var movement = (await supplier.GetAsync($"/api/inventory/movements?productId={productId}")).Expect(HttpStatusCode.OK)
            .Json["items"]!.AsArray().Single()!;
        Assert.Equal("Receipt", Str(movement["type"]));
        Assert.Equal(1250, movement["unitCostMinor"]!.GetValue<long>());
    }

    [Fact]
    public async Task Concurrent_retries_with_the_same_key_apply_the_entry_once()
    {
        var (supplier, _) = await PartiesAsync();
        var body = new { newProduct = NewPart(Scenario.NewEan()), quantity = 4 };
        var key = Key();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EntryAsync(supplier, body, key)));

        Assert.Equal(1, responses.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(responses, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Body?.ToJsonString()));
        Assert.Single(responses.Select(r => Str(r.Json["entryId"])).Distinct());
        var productId = Str(responses[0].Json["product"]!["id"]);
        var stock = (await supplier.GetAsync("/api/inventory/stock")).Json["items"]!.AsArray().Single(i => Str(i!["productId"]) == productId)!;
        Assert.Equal(4, Int(stock["onHand"]));
    }

    [Fact]
    public async Task New_product_data_matching_an_existing_product_receives_stock_on_it()
    {
        var (supplier, _) = await PartiesAsync();
        var ean = Scenario.NewEan();
        var first = (await EntryAsync(supplier, new { newProduct = NewPart(ean), quantity = 5 }, Key())).Expect(HttpStatusCode.Created).Json;

        var second = (await EntryAsync(supplier, new { newProduct = NewPart(ean), quantity = 3, purchasePrice = 9.99m }, Key()))
            .Expect(HttpStatusCode.Created).Json;

        Assert.Equal("MatchedExisting", Str(second["productAction"]));
        Assert.Equal(Str(first["product"]!["id"]), Str(second["product"]!["id"]));
        Assert.Equal(8, Int(second["stock"]!["onHand"])); // received, not synchronized to 3
    }

    [Fact]
    public async Task Conflicting_identifiers_are_rejected_and_nothing_is_saved()
    {
        var (supplier, _) = await PartiesAsync();
        var ean = Scenario.NewEan();
        (await EntryAsync(supplier, new { newProduct = NewPart(ean, quality: "Original"), quantity = 1 }, Key())).Expect(HttpStatusCode.Created);

        // Same EAN but another quality: an aftermarket/service-pack part must never merge into the original.
        var conflict = await EntryAsync(supplier, new
        {
            newProduct = NewPart(ean, quality: "ServicePack"),
            quantity = 7,
            offer = new { supplierSku = "X-" + Scenario.UniqueSuffix(), price = 10m },
        }, Key());

        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("entry.identifier_conflict", conflict.Code);
        var lookup = (await supplier.GetAsync($"/api/inventory/items/lookup?q={ean}")).Expect(HttpStatusCode.OK).Json;
        var match = lookup["matches"]!.AsArray().Single()!;
        Assert.Equal(1, Int(match["ownOnHand"]));
        Assert.Empty(match["ownOffers"]!.AsArray());
    }

    [Fact]
    public async Task Sku_of_another_product_is_a_conflict()
    {
        var (supplier, _) = await PartiesAsync();
        var sku = "SKU-" + Scenario.UniqueSuffix();
        (await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()), offer = new { supplierSku = sku, price = 10m } }, Key()))
            .Expect(HttpStatusCode.Created);
        var other = await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()), quantity = 2, offer = new { supplierSku = sku, price = 11m } }, Key());
        Assert.Equal(HttpStatusCode.Conflict, other.Status);
        Assert.Equal("entry.sku_conflict", other.Code);
    }

    [Fact]
    public async Task Serialized_devices_are_entered_by_unique_imei_atomically()
    {
        var (supplier, shop) = await PartiesAsync();
        await SetModulesAsync(shop, "B2BPurchasing", "Inventory", "ProductImport");
        var phone = new { brand = "Samsung", name = "Galaxy S23 128GB", category = "Smartphone", quality = "NotApplicable", ean = Scenario.NewEan(),
            modelName = "Galaxy S23", compatibleModels = Array.Empty<string>() };
        var imei1 = NewImei();
        var imei2 = NewImei();

        var noImei = await EntryAsync(supplier, new { newProduct = phone, quantity = 2 }, Key());
        Assert.Equal("entry.imei_required", noImei.Code);

        var entry = (await EntryAsync(supplier, new { newProduct = phone, imeis = new[] { imei1, imei2 }, purchasePrice = 300m }, Key()))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal(2, entry["unitIds"]!.AsArray().Count);
        Assert.Equal(2, Int(entry["stock"]!["onHand"]));
        var productId = Str(entry["product"]!["id"]);

        // The same device cannot be stocked twice — not even by another organization.
        var twice = await EntryAsync(shop, new { productId, imeis = new[] { imei1 } }, Key());
        Assert.Equal(HttpStatusCode.Conflict, twice.Status);
        Assert.Equal("unit.imei_in_stock", twice.Code);

        // All or nothing: one duplicate IMEI rejects the whole entry, so the new IMEI is still free afterwards.
        var imei3 = NewImei();
        Assert.Equal("unit.imei_in_stock", (await EntryAsync(supplier, new { productId, imeis = new[] { imei3, imei2 } }, Key())).Code);
        (await EntryAsync(supplier, new { productId, imeis = new[] { imei3 } }, Key())).Expect(HttpStatusCode.Created);

        // B2B offers for serialized stock are not supported yet.
        var withOffer = await EntryAsync(supplier, new { productId, imeis = new[] { NewImei() }, offer = new { supplierSku = "P-" + Scenario.UniqueSuffix(), price = 500m } }, Key());
        Assert.Equal("entry.serialized_offer_unsupported", withOffer.Code);

        var bad = await EntryAsync(supplier, new { productId, imeis = new[] { "123456789012345" } }, Key());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal("validation.failed", bad.Code);
    }

    [Fact]
    public async Task Product_creation_and_offers_need_their_modules_and_tenants_stay_isolated()
    {
        var (supplier, shop) = await PartiesAsync(); // shop: B2BPurchasing + Inventory only
        var existing = (await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()), quantity = 1 }, Key())).Expect(HttpStatusCode.Created).Json;
        var productId = Str(existing["product"]!["id"]);

        Assert.Equal("entry.product_creation_not_allowed", (await EntryAsync(shop, new { newProduct = NewPart(Scenario.NewEan()), quantity = 1 }, Key())).Code);
        Assert.Equal("entry.offers_not_allowed", (await EntryAsync(shop, new { productId, quantity = 1, offer = new { supplierSku = "S1", price = 5m } }, Key())).Code);
        var own = (await EntryAsync(shop, new { productId, quantity = 3 }, Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Selected", Str(own["productAction"]));
        Assert.Equal(3, Int(own["stock"]!["onHand"])); // the shop's own stock; the supplier's 1 unit is not touched

        var supplierWarehouse = Str((await supplier.GetAsync("/api/inventory/warehouses")).Json.AsArray()[0]!["id"]);
        var foreign = await EntryAsync(shop, new { productId, warehouseId = supplierWarehouse, quantity = 1 }, Key());
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("warehouse.not_found", foreign.Code);

        var shopLookup = (await shop.GetAsync($"/api/inventory/items/lookup?q={Scenario.NewEan()}")).Json;
        Assert.False(shopLookup["canCreateProduct"]!.GetValue<bool>());
        Assert.True((await supplier.GetAsync("/api/inventory/items/lookup?q=x")).Json["canCreateProduct"]!.GetValue<bool>());

        var noInventory = await _scenario.RegisterAsync("einkauf", wholesaler: false);
        await _scenario.ApproveAsync(noInventory, "B2BPurchasing");
        Assert.Equal("module.disabled", (await EntryAsync(noInventory, new { productId, quantity = 1 }, Key())).Code);
        Assert.Equal("module.disabled", (await noInventory.GetAsync("/api/inventory/items/lookup?q=x")).Code);
    }

    [Fact]
    public async Task Manual_entries_are_isolated_by_row_level_security_and_insert_only()
    {
        var (supplier, shop) = await PartiesAsync();
        (await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()), quantity = 1 }, Key())).Expect(HttpStatusCode.Created);

        await using (var asShop = await ConnectAsAppAsync(shop.OrganizationId))
            Assert.Equal(0, await CountAsync(asShop, $"SELECT count(*) FROM imports.manual_entries WHERE organization_id = '{supplier.OrganizationId}'"));
        await using (var noTenant = await ConnectAsAppAsync(null))
            Assert.Equal(0, await CountAsync(noTenant, "SELECT count(*) FROM imports.manual_entries"));

        await using var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId);
        Assert.True(await CountAsync(asSupplier, "SELECT count(*) FROM imports.manual_entries") >= 1);
        foreach (var sql in new[] { "UPDATE imports.manual_entries SET quantity_received = 99", "DELETE FROM imports.manual_entries" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier, sql));
            Assert.Equal("42501", ex.SqlState);
        }
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier,
            $"INSERT INTO imports.manual_entries (id, organization_id, idempotency_key, request_hash, product_id, product_action, warehouse_id, quantity_received, unit_ids, offer_action, created_by, created_at, result) " +
            $"SELECT gen_random_uuid(), '{shop.OrganizationId}', 'k', repeat('a', 64), product_id, 'Selected', warehouse_id, 0, '{{}}', 'None', created_by, now(), '{{}}' FROM imports.manual_entries LIMIT 1"));
        Assert.Equal("42501", insert.SqlState); // row for another tenant
    }

    [Fact]
    public async Task Lookup_finds_by_ean_mpn_and_text_with_own_stock_offers_and_hints()
    {
        var (supplier, _) = await PartiesAsync();
        var ean = Scenario.NewEan();
        var mpn = "661-" + Scenario.UniqueSuffix();
        var sku = "L-" + Scenario.UniqueSuffix();
        (await EntryAsync(supplier, new
        {
            newProduct = new { brand = "Apple", name = "Akku iPhone 12 " + mpn, category = "Battery", quality = "Original", ean, mpn, compatibleModels = new[] { "iPhone 12" } },
            quantity = 6,
            offer = new { supplierSku = sku, price = 19.90m },
        }, Key())).Expect(HttpStatusCode.Created);

        var byEan = (await supplier.GetAsync($"/api/inventory/items/lookup?q={ean}")).Expect(HttpStatusCode.OK).Json;
        Assert.True(byEan["isValidEan"]!.GetValue<bool>());
        var match = byEan["matches"]!.AsArray().Single()!;
        Assert.Equal("Ean", Str(match["matchedBy"]));
        Assert.Equal(6, Int(match["ownOnHand"]));
        Assert.Equal(sku, Str(match["ownOffers"]![0]!["supplierSku"]));
        Assert.Equal(sku, Str(match["suggestedSku"]));
        Assert.False(match["imeiRequired"]!.GetValue<bool>());
        Assert.True(match["canManageImages"]!.GetValue<bool>());
        Assert.Null(byEan["newProduct"]);

        var byMpn = (await supplier.GetAsync($"/api/inventory/items/lookup?q={mpn.ToLowerInvariant()}")).Json;
        Assert.Equal("Mpn", Str(byMpn["matches"]![0]!["matchedBy"]));
        var byText = (await supplier.GetAsync($"/api/inventory/items/lookup?q=Akku iPhone 12 {mpn}")).Json;
        Assert.Equal("Text", Str(byText["matches"]![0]!["matchedBy"]));

        var unknown = Scenario.NewEan();
        var none = (await supplier.GetAsync($"/api/inventory/items/lookup?q={unknown}")).Json;
        Assert.Empty(none["matches"]!.AsArray());
        Assert.Equal(unknown, Str(none["newProduct"]!["ean"]));
        Assert.True(none["canCreateProduct"]!.GetValue<bool>());

        Assert.True((await supplier.GetAsync($"/api/inventory/items/lookup?q={NewImei()}")).Json["looksLikeImei"]!.GetValue<bool>());
    }

    // ---- images ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Product_images_upload_serve_reorder_and_remove()
    {
        var (supplier, shop) = await PartiesAsync();
        var sku = "IMG-" + Scenario.UniqueSuffix();
        var entry = (await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()), quantity = 2, offer = new { supplierSku = sku, price = 20m } }, Key()))
            .Expect(HttpStatusCode.Created).Json;
        var productId = Str(entry["product"]!["id"]);
        var path = $"/api/catalog/products/{productId}/images";

        var first = (await UploadAsync(supplier, path, TestImages.Jpeg(1200, 600, exifOrientation: 1))).Expect(HttpStatusCode.Created).Json;
        Assert.True(first["isPrimary"]!.GetValue<bool>());
        Assert.Equal(320, Int(first["thumbnailWidth"]));
        var second = (await UploadAsync(supplier, path, TestImages.Png(200, 200), "image/png")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(1, Int(second["position"]));

        // Served anonymously, re-encoded to WebP, metadata gone, cacheable forever.
        var anonymous = fixture.Factory.CreateClient();
        using (var full = await anonymous.GetAsync(Str(first["fullUrl"])))
        {
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal("image/webp", full.Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", full.Headers.CacheControl!.ToString());
            Assert.True(full.Headers.CacheControl!.Public);
            var bytes = await full.Content.ReadAsByteArrayAsync();
            Assert.False(TestImages.Contains(bytes, TestImages.ExifSecret));
            Assert.False(TestImages.Contains(bytes, "Exif"));

            using var conditional = new HttpRequestMessage(HttpMethod.Get, Str(first["fullUrl"]));
            conditional.Headers.IfNoneMatch.Add(full.Headers.ETag!);
            using var notModified = await anonymous.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        }
        using (var thumb = await anonymous.GetAsync(Str(first["thumbnailUrl"])))
            Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);

        // Listing and detail carry the images.
        var listing = (await shop.GetAsync($"/api/marketplace/offers?search={sku}")).Json["items"]![0]!;
        Assert.Equal(2, Int(listing["imageCount"]));
        Assert.Equal(Str(first["thumbnailUrl"]), Str(listing["primaryThumbnailUrl"]));

        // Reorder: the PNG becomes primary.
        var reordered = (await supplier.PutAsync($"{path}/order", new { imageIds = new[] { Str(second["id"]), Str(first["id"]) } }))
            .Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(Str(second["id"]), Str(reordered[0]!["id"]));
        Assert.True(reordered[0]!["isPrimary"]!.GetValue<bool>());
        Assert.Equal("image.order_mismatch", (await supplier.PutAsync($"{path}/order", new { imageIds = new[] { Str(first["id"]) } })).Code);

        // Remove: no longer served or listed; the other image stays primary.
        (await supplier.SendAsync(HttpMethod.Delete, $"{path}/{Str(second["id"])}")).Expect(HttpStatusCode.NoContent);
        using (var gone = await anonymous.GetAsync(Str(second["fullUrl"])))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        var remaining = (await shop.GetAsync(path)).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(Str(first["id"]), Str(remaining.Single()!["id"]));
        Assert.True(remaining.Single()!["isPrimary"]!.GetValue<bool>());

        // Another organization may not change the canonical product's images; a platform admin (no tenant) may.
        await SetModulesAsync(shop, "B2BPurchasing", "Inventory", "ProductImport");
        var foreign = await UploadAsync(shop, path, TestImages.Png(50, 50), "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.Status);
        Assert.Equal("image.not_product_owner", foreign.Code);
        Assert.Equal("image.not_product_owner", (await shop.SendAsync(HttpMethod.Delete, $"{path}/{Str(first["id"])}")).Code);
        var admin = await _scenario.AdminAsync();
        (await UploadAsync(admin, $"/api/admin/catalog/products/{productId}/images", TestImages.Webp(64, 64), "image/webp")).Expect(HttpStatusCode.Created);
        Assert.Equal(2, (await shop.GetAsync(path)).Json.AsArray().Count);
    }

    [Fact]
    public async Task Image_uploads_are_validated()
    {
        var (supplier, _) = await PartiesAsync();
        var entry = (await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()) }, Key())).Expect(HttpStatusCode.Created).Json;
        var path = $"/api/catalog/products/{Str(entry["product"]!["id"])}/images";

        var text = await UploadAsync(supplier, path, "<?php echo 'not an image'; ?>"u8.ToArray(), "image/jpeg");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, text.Status);
        Assert.Equal("image.unsupported_type", text.Code);

        var gif = await UploadAsync(supplier, path, "GIF89a\u0001\0\u0001\0\0\0\0;"u8.ToArray(), "image/gif");
        Assert.Equal("image.unsupported_type", gif.Code);

        var tooLarge = new byte[ImageLimitsBytes + 1];
        TestImages.Png(10, 10).CopyTo(tooLarge, 0);
        var large = await UploadAsync(supplier, path, tooLarge, "image/png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, large.Status);
        Assert.Equal("image.too_large", large.Code);

        byte[] fakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        Assert.Equal("image.corrupt", (await UploadAsync(supplier, path, fakeJpeg)).Code);

        var missing = await supplier.SendAsync(HttpMethod.Post, path, content: new MultipartFormDataContent { { new StringContent("x"), "other" } });
        Assert.Equal("image.file_missing", missing.Code);

        Assert.Empty((await supplier.GetAsync(path)).Json.AsArray()); // nothing was stored
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(supplier, $"/api/catalog/products/{Guid.NewGuid()}/images", TestImages.Png(5, 5), "image/png")).Status);
    }

    private const long ImageLimitsBytes = 5 * 1024 * 1024;

    [Fact]
    public async Task Product_images_are_protected_at_database_level()
    {
        var (supplier, shop) = await PartiesAsync();
        var entry = (await EntryAsync(supplier, new { newProduct = NewPart(Scenario.NewEan()) }, Key())).Expect(HttpStatusCode.Created).Json;
        var productId = Str(entry["product"]!["id"]);
        (await UploadAsync(supplier, $"/api/catalog/products/{productId}/images", TestImages.Png(40, 40), "image/png")).Expect(HttpStatusCode.Created);

        await using var asShop = await ConnectAsAppAsync(shop.OrganizationId);
        Assert.Equal(1, await CountAsync(asShop, $"SELECT count(*) FROM catalog.product_images WHERE product_id = '{productId}'")); // readable
        Assert.Equal(0, await ExecuteAsync(asShop, $"UPDATE catalog.product_images SET position = 3 WHERE product_id = '{productId}'"));
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asShop,
            $"INSERT INTO catalog.product_images SELECT gen_random_uuid(), product_id, 5, storage_key || 'x', content_type, width, height, size_bytes, " +
            $"thumbnail_storage_key, thumbnail_width, thumbnail_height, thumbnail_size_bytes, sha256, original_format, original_size_bytes, " +
            $"'{shop.OrganizationId}', uploaded_by_user_id, now() FROM catalog.product_images WHERE product_id = '{productId}'"));
        Assert.Equal("42501", insert.SqlState);

        await using var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier, "DELETE FROM catalog.product_images"));
        Assert.Equal("42501", delete.SqlState);
        // Two active images can never share a position (deferred unique constraint, checked at commit).
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier,
            $"INSERT INTO catalog.product_images SELECT gen_random_uuid(), product_id, 0, storage_key || 'y', content_type, width, height, size_bytes, " +
            $"thumbnail_storage_key, thumbnail_width, thumbnail_height, thumbnail_size_bytes, sha256, original_format, original_size_bytes, " +
            $"'{supplier.OrganizationId}', uploaded_by_user_id, now() FROM catalog.product_images WHERE product_id = '{productId}'"));
        Assert.Equal("23505", duplicate.SqlState);
        var ninth = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier,
            $"UPDATE catalog.product_images SET position = 8 WHERE product_id = '{productId}'"));
        Assert.Equal("23514", ninth.SqlState); // at most 8 positions
    }

    // ---- marketplace detail, filters, facets ------------------------------------------------------------------------------

    [Fact]
    public async Task Offer_detail_has_all_product_data_images_and_the_other_sellers_offers()
    {
        var (supplier, shop) = await PartiesAsync();
        var supplier2 = await _scenario.RegisterAsync("grosshandel2", wholesaler: true);
        await _scenario.ApproveAsync(supplier2, "Inventory", "B2BSales");
        var ean = Scenario.NewEan();
        var entry = (await EntryAsync(supplier, new
        {
            newProduct = new { brand = "Apple", name = "iPhone 14 Display", category = "Display", quality = "ServicePack", ean, mpn = "661-" + Scenario.UniqueSuffix(),
                modelName = "iPhone 14", compatibleModels = new[] { "iPhone 14", "iPhone 14 Plus" } },
            quantity = 3,
            offer = new { supplierSku = "D-" + Scenario.UniqueSuffix(), price = 99m, deliveryDays = 2 },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        var productId = Str(entry["product"]!["id"]);
        (await UploadAsync(supplier, $"/api/catalog/products/{productId}/images", TestImages.Jpeg(400, 300))).Expect(HttpStatusCode.Created);
        var other = (await EntryAsync(supplier2, new { productId, quantity = 1, offer = new { supplierSku = "O-" + Scenario.UniqueSuffix(), price = 95m } }, Key()))
            .Expect(HttpStatusCode.Created).Json;

        var detail = (await shop.GetAsync($"/api/marketplace/offers/{Str(entry["offer"]!["id"])}/detail")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(9900, detail["offer"]!["priceMinor"]!.GetValue<long>());
        var product = detail["product"]!;
        Assert.Equal("iPhone 14", Str(product["modelName"]));
        Assert.Equal(2, product["compatibleModels"]!.AsArray().Count);
        Assert.Equal(ean, Str(product["ean"]));
        var image = product["images"]!.AsArray().Single()!;
        Assert.StartsWith("/api/catalog/images/", Str(image["thumbnailUrl"]));
        Assert.EndsWith("/full", Str(image["fullUrl"]));
        var others = detail["otherOffers"]!.AsArray();
        Assert.Equal(Str(other["offer"]!["id"]), Str(others.Single()!["offerId"]));

        Assert.Equal("offer.not_found", (await shop.GetAsync($"/api/marketplace/offers/{Guid.NewGuid()}/detail")).Code);
    }

    [Fact]
    public async Task Browse_filters_and_facet_counts()
    {
        var (supplier, shop) = await PartiesAsync();
        var s = Scenario.UniqueSuffix();
        string brandA = $"MarkeA{s}", brandB = $"MarkeB{s}", model = $"Testphone {s}";
        async Task<string> Add(string brand, string quality, string condition, decimal price, int days, string[] compatible)
        {
            var e = (await EntryAsync(supplier, new
            {
                newProduct = new { brand, name = $"{brand} Teil {Scenario.UniqueSuffix()}", category = "Display", quality, ean = Scenario.NewEan(), compatibleModels = compatible },
                quantity = 2,
                offer = new { supplierSku = $"F-{Scenario.UniqueSuffix()}", price, deliveryDays = days, condition },
            }, Key())).Expect(HttpStatusCode.Created).Json;
            return Str(e["product"]!["id"]);
        }
        var a1 = await Add(brandA, "Original", "New", 10m, 1, [model]);
        await Add(brandA, "Aftermarket", "Refurbished", 30m, 5, []);
        await Add(brandB, "Original", "New", 50m, 10, [model + " Pro"]);
        (await UploadAsync(supplier, $"/api/catalog/products/{a1}/images", TestImages.Png(30, 30), "image/png")).Expect(HttpStatusCode.Created);

        var seller = supplier.OrganizationId;
        async Task<int> Count(string filter) =>
            Int((await shop.GetAsync($"/api/marketplace/offers?sellerId={seller}&{filter}")).Expect(HttpStatusCode.OK).Json["totalCount"]);

        Assert.Equal(3, await Count($"brand={brandA}&brand={brandB}"));
        Assert.Equal(2, await Count($"brand={brandA}"));
        Assert.Equal(0, await Count($"brand={brandA.ToLowerInvariant()}")); // exact
        Assert.Equal(2, await Count($"brand={brandA}&brand={brandB}&quality=Original"));
        Assert.Equal(1, await Count($"brand={brandA}&condition=Refurbished"));
        Assert.Equal(2, await Count($"brand={brandA}&brand={brandB}&priceMin=10&priceMax=30"));
        Assert.Equal(1, await Count($"brand={brandA}&brand={brandB}&priceMin=30.01"));
        Assert.Equal(2, await Count($"brand={brandA}&brand={brandB}&maxDeliveryDays=5"));
        Assert.Equal(1, await Count($"compatibleModel={Uri.EscapeDataString(model.ToLowerInvariant().Replace(" ", "-"))}")); // normalized, exact model
        Assert.Equal(1, await Count($"brand={brandA}&brand={brandB}&hasImages=true"));
        Assert.Equal(2, await Count($"brand={brandA}&brand={brandB}&hasImages=false"));
        Assert.Equal(0, Int((await shop.GetAsync($"/api/marketplace/offers?sellerId={Guid.NewGuid()}&brand={brandA}")).Json["totalCount"]));

        var invalid = await shop.GetAsync("/api/marketplace/offers?priceMin=1.005");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.Equal("validation.failed", invalid.Code);

        // Facets: each dimension ignores its own filter (multi-select), all others apply.
        var facets = (await shop.GetAsync($"/api/marketplace/facets?sellerId={seller}&brand={brandA}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, Int(facets["total"]));
        var brands = facets["brands"]!.AsArray();
        Assert.Equal(2, Int(brands.Single(b => Str(b!["value"]) == brandA)!["count"]));
        Assert.Equal(1, Int(brands.Single(b => Str(b!["value"]) == brandB)!["count"]));
        Assert.Equal(2, brands.Count); // seller filter applies to the brand facet
        var qualities = facets["qualities"]!.AsArray();
        Assert.Equal(1, Int(qualities.Single(q => Str(q!["value"]) == "Original")!["count"]));
        Assert.Equal(1, Int(qualities.Single(q => Str(q!["value"]) == "Aftermarket")!["count"]));
        Assert.Equal(1, Int(facets["conditions"]!.AsArray().Single(c => Str(c!["value"]) == "Refurbished")!["count"]));
        Assert.Equal(2, Int(facets["categories"]!.AsArray().Single(c => Str(c!["value"]) == "Display")!["count"]));
        var sellers = facets["sellers"]!.AsArray();
        Assert.Equal(2, Int(sellers.Single(x => Str(x!["id"]) == seller.ToString())!["count"]));
        Assert.Equal(1000, facets["price"]!["minMinor"]!.GetValue<long>());
        Assert.Equal(3000, facets["price"]!["maxMinor"]!.GetValue<long>());
        Assert.Equal(1, Int(facets["withImages"]));

        var priced = (await shop.GetAsync($"/api/marketplace/facets?sellerId={seller}&brand={brandA}&brand={brandB}&priceMax=20")).Json;
        Assert.Equal(1, Int(priced["total"]));
        Assert.Equal(5000, priced["price"]!["maxMinor"]!.GetValue<long>()); // the price range ignores the price filter

        var empty = (await shop.GetAsync($"/api/marketplace/facets?sellerId={Guid.NewGuid()}&brand={brandA}")).Json;
        Assert.Equal(0, Int(empty["total"]));
        Assert.Null(empty["price"]);
    }

    [Fact]
    public void Marketplace_has_no_endpoint_that_creates_products()
    {
        var endpoints = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var marketplace = endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/api/marketplace", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(marketplace);
        var writes = marketplace.Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
            .Any(m => m != "GET") == true).Select(e => e.RoutePattern.RawText).ToList();
        Assert.Empty(writes);
    }
}
