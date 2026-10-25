using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>ADR-0017: PUT /api/catalog/products/{id} for the creating organization (real PostgreSQL, RLS on).</summary>
[Collection(HandyNetCollection.Name)]
public sealed class ProductEditTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private static string Str(JsonNode? node) => node!.GetValue<string>();

    private static async Task<JsonNode> CreateAsync(ApiClient client, string ean, string? mpn = null, string brand = "Apple", string quality = "ServicePack")
    {
        var entry = (await client.PostAsync("/api/inventory/items", new
        {
            newProduct = new { brand, name = $"{brand} Display {ean[^4..]}", category = "Display", quality, ean, mpn, compatibleModels = new[] { "iPhone 13" } },
            quantity = 3,
        }, new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") })).Expect(HttpStatusCode.Created).Json;
        return (await client.GetAsync($"/api/catalog/products/{Str(entry["product"]!["id"])}")).Expect(HttpStatusCode.OK).Json;
    }

    /// <summary>The product as PUT body (all fields), with changes applied.</summary>
    private static JsonObject Edit(JsonNode product, Action<JsonObject> change)
    {
        var body = new JsonObject
        {
            ["brand"] = product["brand"]?.DeepClone(),
            ["name"] = product["name"]?.DeepClone(),
            ["category"] = product["category"]?.DeepClone(),
            ["quality"] = product["quality"]?.DeepClone(),
            ["mpn"] = product["mpn"]?.DeepClone(),
            ["ean"] = product["ean"]?.DeepClone(),
            ["modelName"] = product["modelName"]?.DeepClone(),
            ["compatibleModels"] = product["compatibleModels"]?.DeepClone(),
            ["version"] = product["version"]?.DeepClone(),
        };
        change(body);
        return body;
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

    private async Task<object?> AsOwnerAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Creating_organization_edits_its_product_with_optimistic_concurrency_and_audit()
    {
        var (supplier, _) = await _scenario.SupplierAndShopAsync();
        var product = await CreateAsync(supplier, Scenario.NewEan());
        var id = Str(product["id"]);
        Assert.True(product["createdByCurrentOrganization"]!.GetValue<bool>());
        Assert.True(product["canEdit"]!.GetValue<bool>());

        var newEan = Scenario.NewEan();
        var mpn = "661-" + Scenario.UniqueSuffix();
        var updated = (await supplier.PutAsync($"/api/catalog/products/{id}", Edit(product, b =>
        {
            b["name"] = "  iPhone 13 Display OLED  ";
            b["ean"] = newEan;
            b["mpn"] = mpn;
            b["quality"] = "Original";
            b["modelName"] = "iPhone 13";
            b["compatibleModels"] = new JsonArray("iPhone 13", "iPhone 13 Pro");
        }))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("iPhone 13 Display OLED", Str(updated["name"]));
        Assert.Equal(newEan, Str(updated["ean"]));
        Assert.Equal("Original", Str(updated["quality"]));
        Assert.NotEqual(product["version"]!.GetValue<uint>(), updated["version"]!.GetValue<uint>());

        // Visible everywhere the canonical product is used (here: own stock and the lookup by the new EAN / model).
        var stock = (await supplier.GetAsync("/api/inventory/stock")).Json["items"]!.AsArray().Single(r => Str(r!["productId"]) == id)!;
        Assert.Equal("iPhone 13 Display OLED", Str(stock["productName"]));
        Assert.Equal(id, Str((await supplier.GetAsync($"/api/inventory/items/lookup?q={newEan}")).Json["matches"]![0]!["product"]!["id"]));

        // A stale version is refused; so is a missing one.
        var stale = await supplier.PutAsync($"/api/catalog/products/{id}", Edit(product, b => b["name"] = "Veraltet"));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("concurrency", stale.Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await supplier.PutAsync($"/api/catalog/products/{id}", Edit(updated, b => b.Remove("version")))).Status);

        // Same validation as creating: invalid EAN, no identifier, IMEI category switch.
        Assert.Equal("product.invalid_ean", (await supplier.PutAsync($"/api/catalog/products/{id}", Edit(updated, b => b["ean"] = "4006381333932"))).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await supplier.PutAsync($"/api/catalog/products/{id}", Edit(updated, b => { b["ean"] = null; b["mpn"] = null; }))).Status);
        Assert.Equal("product.category_stock_mode",
            (await supplier.PutAsync($"/api/catalog/products/{id}", Edit(updated, b => b["category"] = "Smartphone"))).Code);

        // Unchanged data: 200 without a new version.
        var same = (await supplier.PutAsync($"/api/catalog/products/{id}", Edit(updated, _ => { }))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(updated["version"]!.GetValue<uint>(), same["version"]!.GetValue<uint>());

        // Audited with before/after values and the acting organization.
        var audit = (string)(await AsOwnerAsync($"""
            SELECT changes::text FROM infra.audit_log
             WHERE entity_type = 'Product' AND entity_id = '{id}' AND action = 'Modified' AND organization_id = '{supplier.OrganizationId}'
             ORDER BY id DESC LIMIT 1
            """))!;
        var changes = JsonNode.Parse(audit)!;
        Assert.Equal("iPhone 13 Display OLED", Str(changes["Name"]!["to"]));
        Assert.Equal(Str(product["name"]), Str(changes["Name"]!["from"]));
        Assert.Equal(newEan, Str(changes["Ean"]!["to"]));
    }

    [Fact]
    public async Task Only_the_creating_organization_may_edit()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var product = await CreateAsync(supplier, Scenario.NewEan());
        var id = Str(product["id"]);

        // The other tenant reads it (shared catalog) but may not change it, even with module + permission.
        await SetModulesAsync(shop, "B2BPurchasing", "Inventory", "ProductImport");
        var seen = (await shop.GetAsync($"/api/catalog/products/{id}")).Expect(HttpStatusCode.OK).Json;
        Assert.False(seen["createdByCurrentOrganization"]!.GetValue<bool>());
        Assert.False(seen["canEdit"]!.GetValue<bool>());
        var foreign = await shop.PutAsync($"/api/catalog/products/{id}", Edit(seen, b => b["name"] = "Fremd"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.Status);
        Assert.Equal("product.not_owner", foreign.Code);

        // Without the module ProductImport the owner may not edit either (like creating products / images).
        var supplier2 = await _scenario.RegisterAsync("grosshandel-ohne-import", wholesaler: true);
        await _scenario.ApproveAsync(supplier2, "Inventory", "ProductImport");
        var own = await CreateAsync(supplier2, Scenario.NewEan());
        (await (await _scenario.AdminAsync()).PutAsync($"/api/admin/organizations/{supplier2.OrganizationId}/modules", new { modules = new[] { "Inventory" } }))
            .Expect(HttpStatusCode.OK);
        var detail = (await supplier2.GetAsync($"/api/catalog/products/{Str(own["id"])}")).Json;
        Assert.True(detail["createdByCurrentOrganization"]!.GetValue<bool>());
        Assert.False(detail["canEdit"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await supplier2.PutAsync($"/api/catalog/products/{Str(own["id"])}", Edit(detail, b => b["name"] = "x"))).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await supplier.PutAsync($"/api/catalog/products/{Guid.NewGuid()}", Edit(product, _ => { }))).Status);
        Assert.Equal(Str(product["name"]), Str((await supplier.GetAsync($"/api/catalog/products/{id}")).Json["name"]));
    }

    [Fact]
    public async Task Identity_changes_must_not_collide_with_another_product()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var mpnA = "A-" + Scenario.UniqueSuffix();
        var a = await CreateAsync(supplier, Scenario.NewEan(), mpnA);
        var b = await CreateAsync(supplier, Scenario.NewEan(), "B-" + Scenario.UniqueSuffix());
        var bId = Str(b["id"]);

        // EAN of another product (here: one created by a different organization) → refused with its id.
        await SetModulesAsync(shop, "B2BPurchasing", "Inventory", "ProductImport");
        var foreign = await CreateAsync(shop, Scenario.NewEan(), brand: "Samsung");
        var eanTaken = await supplier.PutAsync($"/api/catalog/products/{bId}", Edit(b, x => x["ean"] = Str(foreign["ean"])));
        Assert.Equal(HttpStatusCode.Conflict, eanTaken.Status);
        Assert.Equal("product.ean_taken", eanTaken.Code);
        Assert.Equal(Str(foreign["id"]), Str(eanTaken.Json["productId"]));

        // Brand + part number + quality of another product (part number compared normalized) → refused.
        var identity = await supplier.PutAsync($"/api/catalog/products/{bId}", Edit(b, x => x["mpn"] = mpnA.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(HttpStatusCode.Conflict, identity.Status);
        Assert.Equal("product.identity_taken", identity.Code);
        Assert.Equal(Str(a["id"]), Str(identity.Json["productId"]));

        // The same part number with another quality is a different product → allowed.
        var otherQuality = await supplier.PutAsync($"/api/catalog/products/{bId}", Edit(b, x => { x["mpn"] = mpnA; x["quality"] = "Aftermarket"; }));
        Assert.Equal(HttpStatusCode.OK, otherQuality.Status);
        Assert.Equal(Str(b["ean"]), Str((await supplier.GetAsync($"/api/catalog/products/{bId}")).Json["ean"]));
    }

    [Fact]
    public async Task Products_are_protected_at_database_level()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var id = Str((await CreateAsync(supplier, Scenario.NewEan()))["id"]);

        await using var asShop = await ConnectAsAppAsync(shop.OrganizationId);
        await using (var read = new NpgsqlCommand($"SELECT count(*) FROM catalog.products WHERE id = '{id}'", asShop))
            Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!); // shared catalog: readable
        Assert.Equal(0, await ExecuteAsync(asShop, $"UPDATE catalog.products SET name = 'Fremd' WHERE id = '{id}'"));
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asShop,
            $"INSERT INTO catalog.products (id, brand, brand_key, name, category, quality, mpn, mpn_key, compatible_models, model_keys, created_at, created_by_organization_id) " +
            $"VALUES (gen_random_uuid(), 'X', 'X', 'Y', 'Other', 'NotApplicable', 'Z-{Guid.NewGuid():N}', 'Z{Guid.NewGuid():N}', '{{}}', '{{}}', now(), '{supplier.OrganizationId}')"));
        Assert.Equal("42501", insert.SqlState); // a tenant inserts only products created by itself

        await using var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId);
        Assert.Equal(1, await ExecuteAsync(asSupplier, $"UPDATE catalog.products SET name = name WHERE id = '{id}'"));
        var handOver = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier,
            $"UPDATE catalog.products SET created_by_organization_id = '{shop.OrganizationId}' WHERE id = '{id}'"));
        Assert.Equal("42501", handOver.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asSupplier, $"DELETE FROM catalog.products WHERE id = '{id}'"));
        Assert.Equal("42501", delete.SqlState);
    }

    [Fact]
    public async Task Own_offers_can_be_listed_per_product()
    {
        var (supplier, _) = await _scenario.SupplierAndShopAsync();
        var a = Str((await CreateAsync(supplier, Scenario.NewEan()))["id"]);
        var b = Str((await CreateAsync(supplier, Scenario.NewEan()))["id"]);
        foreach (var product in new[] { a, b })
            (await supplier.PostAsync("/api/offers", new
            {
                productId = product, supplierSku = "P-" + Scenario.UniqueSuffix(), price = 19.90m, minOrderQuantity = 1, deliveryDays = 2,
                condition = "New", publish = false,
            })).Expect(HttpStatusCode.Created);

        var items = (await supplier.GetAsync($"/api/offers?productId={a}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal(a, Str(Assert.Single(items)!["productId"]));

        // Stock of one product (every warehouse) for the Lager product panel.
        var stock = (await supplier.GetAsync($"/api/inventory/stock?productId={b}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal(3, Assert.Single(stock)!["onHand"]!.GetValue<int>());
    }
}
