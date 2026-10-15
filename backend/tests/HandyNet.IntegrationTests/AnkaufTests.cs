using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using ClosedXML.Excel;
using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class AnkaufTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    /// <summary>Unique brand per test so lists from other tests never interfere.</summary>
    private static string NewBrand() => "Brand" + Scenario.UniqueSuffix();

    private static byte[] PriceListXlsx(params (string Brand, string Model, string Storage, object? Price)[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Mobilfunk");
        sheet.Cell(1, 1).Value = "Hersteller"; sheet.Cell(1, 2).Value = "Modell"; sheet.Cell(1, 3).Value = "Speicher"; sheet.Cell(1, 4).Value = "Preis";
        for (var i = 0; i < rows.Length; i++)
        {
            var (brand, model, storage, price) = rows[i];
            sheet.Cell(i + 2, 1).Value = brand;
            sheet.Cell(i + 2, 2).Value = model;
            sheet.Cell(i + 2, 3).Value = storage;
            if (price is decimal d) sheet.Cell(i + 2, 4).Value = d;
            else if (price is string s) sheet.Cell(i + 2, 4).Value = s;
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static async Task<ApiResponse> UploadAsync(ApiClient admin, byte[] xlsx, DateTimeOffset from, DateTimeOffset until)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(xlsx);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "ankauf.xlsx");
        form.Add(new StringContent("Test " + from.ToString("O", CultureInfo.InvariantCulture)), "label");
        form.Add(new StringContent(from.ToString("O", CultureInfo.InvariantCulture)), "validFrom");
        form.Add(new StringContent(until.ToString("O", CultureInfo.InvariantCulture)), "validUntil");
        return await admin.SendAsync(HttpMethod.Post, "/api/admin/ankauf/price-lists", content: form);
    }

    private static async Task<List<(long Id, string Model, string Change, string Decision)>> EntriesAsync(ApiClient admin, string listId)
    {
        var json = (await admin.GetAsync($"/api/admin/ankauf/price-lists/{listId}/entries?pageSize=200")).Expect(HttpStatusCode.OK).Json;
        return json["items"]!.AsArray().Select(e => (e!["id"]!.GetValue<long>(), e["model"]!.GetValue<string>(),
            e["change"]!.GetValue<string>(), e["decision"]!.GetValue<string>())).ToList();
    }

    private static async Task ApproveAllPendingAsync(ApiClient admin, string listId)
    {
        var pending = (await EntriesAsync(admin, listId)).Where(e => e.Decision == "Pending").Select(e => e.Id).ToArray();
        if (pending.Length > 0)
            (await admin.PostAsync($"/api/admin/ankauf/price-lists/{listId}/decisions", new { entryIds = pending, approve = true })).Expect(HttpStatusCode.OK);
    }

    private async Task<ApiClient> DealerAsync()
    {
        var shop = await _scenario.RegisterAsync("ankaufshop", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Ankauf");
        return shop;
    }

    private static async Task<string> ModelKeyAsync(ApiClient dealer, string search) =>
        (await dealer.GetAsync($"/api/ankauf/models?search={Uri.EscapeDataString(search)}")).Expect(HttpStatusCode.OK)
        .Json.AsArray().Single()!["modelKey"]!.GetValue<string>();

    [Fact]
    public async Task Monthly_list_review_publish_and_quote_with_preserved_versions()
    {
        var admin = await _scenario.AdminAsync();
        var dealer = await DealerAsync();
        var brand = NewBrand();
        var now = DateTimeOffset.UtcNow;

        // September: every model is new → needs approval before anything is quotable.
        var september = (await UploadAsync(admin, PriceListXlsx(
                (brand, "Phone A", "128 GB", 300m), (brand, "Phone B", "64 GB", 200m), (brand, "Phone C", "64 GB", 100m), (brand, "Phone D", "64 GB", 100m)),
            now.AddDays(-20), now.AddDays(10))).Expect(HttpStatusCode.Created).Json;
        var septemberId = september["id"]!.GetValue<string>();
        Assert.Equal(4, september["summary"]!["new"]!.GetValue<int>());
        Assert.Equal("ankauf.pending_decisions", (await admin.PostAsync($"/api/admin/ankauf/price-lists/{septemberId}/publish")).Code);
        Assert.Empty((await dealer.GetAsync($"/api/ankauf/models?search={brand}")).Json.AsArray()); // nothing published yet
        await ApproveAllPendingAsync(admin, septemberId);
        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{septemberId}/publish")).Expect(HttpStatusCode.OK);

        var phoneA = await ModelKeyAsync(dealer, $"{brand} Phone A");
        var quote = (await dealer.PostAsync("/api/ankauf/quotes", new
        {
            modelKey = phoneA, condition = "Neu", defects = new[] { "HousingDamage" }, batteryHealthPercent = 90, consumerPrice = 250m,
            imei = "490154203237518",
        })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(30000, quote["referenceGutMinor"]!.GetValue<long>());
        Assert.Equal(32400, quote["referencePurchasePriceMinor"]!.GetValue<long>()); // 300 × 1.2 × 0.9
        Assert.Equal(27540, quote["maxConsumerPriceMinor"]!.GetValue<long>());       // − 15 % dealer margin
        Assert.Equal(7400, quote["dealerMarginMinor"]!.GetValue<long>());
        Assert.Contains("physischer Prüfung", quote["notice"]!.GetValue<string>());
        var septemberVersion = quote["priceVersionId"]!.GetValue<string>();

        // October: A +10 % (needs approval, we reject), B −10 % (automatic), C −40 % (needs approval, we approve),
        // D missing (no price row), E new (we approve).
        var october = (await UploadAsync(admin, PriceListXlsx(
                (brand, "Phone A", "128 GB", 330m), (brand, "Phone B", "64 GB", 180m), (brand, "Phone C", "64 GB", 60m),
                (brand, "Phone D", "64 GB", "auf Anfrage"), (brand, "Phone E", "256 GB", 500m)),
            now.AddDays(-1), now.AddDays(29))).Expect(HttpStatusCode.Created).Json;
        var octoberId = october["id"]!.GetValue<string>();
        Assert.Equal(1, october["summary"]!["skipped"]!.GetValue<int>());
        var entries = await EntriesAsync(admin, octoberId);
        Assert.Equal(("Increase", "Pending"), entries.Where(e => e.Model == "Phone A").Select(e => (e.Change, e.Decision)).Single());
        Assert.Equal(("Decrease", "AutoAccepted"), entries.Where(e => e.Model == "Phone B").Select(e => (e.Change, e.Decision)).Single());
        Assert.Equal(("LargeDecrease", "Pending"), entries.Where(e => e.Model == "Phone C").Select(e => (e.Change, e.Decision)).Single());
        Assert.Equal(("New", "Pending"), entries.Where(e => e.Model == "Phone E").Select(e => (e.Change, e.Decision)).Single());

        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{octoberId}/decisions",
            new { entryIds = new[] { entries.Single(e => e.Model == "Phone A").Id }, approve = false })).Expect(HttpStatusCode.OK);
        await ApproveAllPendingAsync(admin, octoberId);
        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{octoberId}/publish")).Expect(HttpStatusCode.OK);

        // Only the current (October) list counts: A (rejected) and D (no price) are no longer quotable,
        // although September nominally runs for ten more days.
        var models = (await dealer.GetAsync($"/api/ankauf/models?search={brand}")).Json.AsArray().Select(m => m!["model"]!.GetValue<string>()).ToList();
        Assert.Equal(["Phone B", "Phone C", "Phone E"], models);
        Assert.Equal("ankauf.no_valid_price", (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = phoneA, condition = "Gut" })).Code);

        // The September quote is unchanged and still points at its version.
        var saved = (await dealer.GetAsync("/api/ankauf/quotes")).Json.AsArray().Single(q => q!["id"]!.GetValue<string>() == quote["id"]!.GetValue<string>())!;
        Assert.Equal(septemberVersion, saved["priceVersionId"]!.GetValue<string>());
        Assert.Equal(32400, saved["referencePurchasePriceMinor"]!.GetValue<long>());

        var phoneB = await ModelKeyAsync(dealer, $"{brand} Phone B");
        var octoberQuote = (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = phoneB, condition = "Gut" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(18000, octoberQuote["referencePurchasePriceMinor"]!.GetValue<long>());
    }

    [Fact]
    public async Task Expired_list_offers_nothing_and_access_is_restricted()
    {
        var admin = await _scenario.AdminAsync();
        var brand = NewBrand();
        var expired = (await UploadAsync(admin, PriceListXlsx((brand, "Old", "64 GB", 50m)),
            DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow.AddDays(-10))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        await ApproveAllPendingAsync(admin, expired);
        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{expired}/publish")).Expect(HttpStatusCode.OK);

        var dealer = await DealerAsync();
        Assert.Empty((await dealer.GetAsync($"/api/ankauf/models?search={brand}")).Json.AsArray());

        var withoutModule = await _scenario.RegisterAsync("ohneankauf", wholesaler: false);
        await _scenario.ApproveAsync(withoutModule, "B2BPurchasing");
        Assert.Equal("module.disabled", (await withoutModule.GetAsync("/api/ankauf/models")).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await dealer.GetAsync("/api/admin/ankauf/price-lists")).Status);
    }

    [Fact]
    public async Task New_policy_versions_apply_to_new_quotes_only_and_history_is_immutable()
    {
        var admin = await _scenario.AdminAsync();
        var dealer = await DealerAsync();
        var brand = NewBrand();
        var list = (await UploadAsync(admin, PriceListXlsx((brand, "Phone P", "128 GB", 200m)),
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        await ApproveAllPendingAsync(admin, list);
        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{list}/publish")).Expect(HttpStatusCode.OK);
        var key = await ModelKeyAsync(dealer, $"{brand} Phone P");

        var before = (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Neu" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(24000, before["referencePurchasePriceMinor"]!.GetValue<long>());

        var active = (await admin.GetAsync("/api/admin/ankauf/policies")).Json.AsArray().Single(p => p!["isActive"]!.GetValue<bool>())!;
        var settings = active["settings"]!.DeepClone();
        settings["conditionAdjustments"]!["Neu"] = 1000;
        (await admin.PostAsync("/api/admin/ankauf/policies", new { settings })).Expect(HttpStatusCode.Created);
        try
        {
            var after = (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Neu" })).Expect(HttpStatusCode.Created).Json;
            Assert.Equal(22000, after["referencePurchasePriceMinor"]!.GetValue<long>());
            Assert.True(after["policyNumber"]!.GetValue<int>() > before["policyNumber"]!.GetValue<int>());
        }
        finally
        {
            // Policies are global: restore the original values as a newer version so other tests see the defaults.
            (await admin.PostAsync("/api/admin/ankauf/policies", new { settings = active["settings"] })).Expect(HttpStatusCode.Created);
        }

        // Price versions, policies and quotes cannot be edited — not even by the owner role.
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        foreach (var table in new[] { "ankauf.price_versions", "ankauf.condition_policies", "ankauf.quotes" })
        {
            await using var update = new NpgsqlCommand($"UPDATE {table} SET created_at = created_at", owner);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        }
    }
}
