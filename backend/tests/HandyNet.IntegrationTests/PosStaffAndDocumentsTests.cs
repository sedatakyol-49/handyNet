using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Tests.Support;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// POS Phase 2c open items: product images on tiles, cashier switching with PIN, manager approval, e-receipts, invoices
/// for POS sales and cash rounding (real PostgreSQL, RLS on).
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosStaffAndDocumentsTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);
    private const string StaffPassword = "Kassierer-Passwort-1";

    private static Dictionary<string, string> Key() => new() { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") };

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    private async Task<ApiClient> PosShopAsync()
    {
        var shop = await _scenario.RegisterAsync("kassenshop", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Pos", "Inventory", "ProductImport");
        return shop;
    }

    private static async Task<Guid> DefaultWarehouseAsync(ApiClient shop)
    {
        var warehouses = (await shop.GetAsync("/api/inventory/warehouses")).Expect(HttpStatusCode.OK).Json.AsArray();
        if (warehouses.Count > 0)
            return Id(warehouses.First(w => w!["isDefault"]!.GetValue<bool>())!);
        return Id((await shop.PostAsync("/api/inventory/warehouses", new { code = "MAIN", name = "Hauptlager" })).Expect(HttpStatusCode.Created).Json);
    }

    private static async Task<Guid> StockProductAsync(ApiClient shop, int quantity, string name)
    {
        var product = (await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = $"{name} {Scenario.UniqueSuffix()}", category = "Accessory", quality = "Aftermarket", ean = Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json;
        var productId = Id(product);
        var warehouseId = await DefaultWarehouseAsync(shop);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId, delta = quantity, reason = "Wareneingang", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        return productId;
    }

    private static async Task<Guid> RegisterAsync(ApiClient shop, string name = "Kasse 1") =>
        Id((await shop.PostAsync("/api/pos/registers", new { name })).Expect(HttpStatusCode.Created).Json);

    private static async Task<Guid> OpenSessionAsync(ApiClient client, Guid register, long openingCashMinor = 100_00) =>
        Id((await client.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor })).Expect(HttpStatusCode.Created).Json);

    private static async Task<ApiResponse> UploadImageAsync(ApiClient client, Guid productId, byte[] data)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "foto.png");
        return await client.SendAsync(HttpMethod.Post, $"/api/catalog/products/{productId}/images", content: form);
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- 1. product images on POS tiles ------------------------------------------------------------------------------------

    [Fact]
    public async Task Product_grid_carries_the_primary_image_thumbnail_from_the_catalog()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var withImages = await StockProductAsync(shop, 3, "Bild");
        var withoutImage = await StockProductAsync(shop, 3, "Ohne");
        var first = (await UploadImageAsync(shop, withImages, TestImages.Png(60, 60))).Expect(HttpStatusCode.Created).Json;
        var second = (await UploadImageAsync(shop, withImages, TestImages.Png(80, 40))).Expect(HttpStatusCode.Created).Json;

        async Task<JsonNode> TileAsync(Guid productId) =>
            (await shop.GetAsync($"/api/pos/registers/{register}/products?pageSize=100")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Single(i => Id(i!, "productId") == productId)!;

        Assert.Equal(first["thumbnailUrl"]!.GetValue<string>(), (await TileAsync(withImages))["imageUrl"]!.GetValue<string>());
        Assert.Null((await TileAsync(withoutImage))["imageUrl"]);

        // Reordering makes the second image primary: the tile follows.
        (await shop.PutAsync($"/api/catalog/products/{withImages}/images/order", new { imageIds = new[] { Id(second), Id(first) } }))
            .Expect(HttpStatusCode.OK);
        var url = (await TileAsync(withImages))["imageUrl"]!.GetValue<string>();
        Assert.Equal($"/api/catalog/images/{Id(second)}/thumb", url);

        // The URL serves the thumbnail without authentication (public, immutable).
        var anonymous = fixture.CreateClient();
        var file = await anonymous.GetFileAsync(url);
        Assert.Equal(HttpStatusCode.OK, file.Status);
        Assert.Equal("image/webp", file.MediaType);
    }
}
