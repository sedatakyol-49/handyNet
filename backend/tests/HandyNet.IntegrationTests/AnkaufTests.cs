using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using ClosedXML.Excel;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Tests.Support;
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
        await _scenario.ApproveAsync(shop, "Ankauf", "Inventory");
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
            DateTimeOffset.UtcNow.AddSeconds(-1), now.AddDays(29))).Expect(HttpStatusCode.Created).Json;
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
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(30))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
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

    // ---- device intake ------------------------------------------------------------------------------

    private static readonly object AdultSeller = new
    {
        firstName = "Max", lastName = "Mustermann", street = "Hauptstr. 5", postalCode = "10115", city = "Berlin",
        birthDate = "1990-05-01", idDocumentType = "IdCard", idDocumentNumber = "L01X00T47",
    };

    /// <summary>Unique valid IMEI (Luhn) per call.</summary>
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

    /// <summary>Publishes a current list with one model; returns the dealer, the model key, the admin and the brand.</summary>
    private async Task<(ApiClient Dealer, string ModelKey, ApiClient Admin, string Brand)> PricedModelAsync(decimal price = 300m)
    {
        var admin = await _scenario.AdminAsync();
        var brand = NewBrand();
        var list = (await UploadAsync(admin, PriceListXlsx((brand, "Phone I", "128 GB", price)),
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(30))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        await ApproveAllPendingAsync(admin, list);
        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{list}/publish")).Expect(HttpStatusCode.OK);
        var dealer = await DealerAsync();
        return (dealer, await ModelKeyAsync(dealer, $"{brand} Phone I"), admin, brand);
    }

    [Fact]
    public async Task Intake_from_quote_to_inspected_purchase_and_serialized_stock()
    {
        var (dealer, key, admin, brand) = await PricedModelAsync();
        var imei = NewImei();
        var quote = (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "SehrGut" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(31500, quote["platformOfferMinor"]!.GetValue<long>()); // 300 × 1.05

        var intake = (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = quote["id"]!.GetValue<string>(), imei, seller = AdultSeller }))
            .Expect(HttpStatusCode.Created).Json;
        var intakeId = intake["id"]!.GetValue<string>();
        Assert.Equal("Max Mustermann", intake["sellerName"]!.GetValue<string>());
        Assert.Equal("ankauf.not_inspected",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 100m, paymentMethod = "Cash" })).Code);

        // A new monthly list after the quote must not change the inspection basis.
        var newer = (await UploadAsync(admin, PriceListXlsx((brand, "Phone I", "128 GB", 250m)),
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(30))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        await ApproveAllPendingAsync(admin, newer);
        (await admin.PostAsync($"/api/admin/ankauf/price-lists/{newer}/publish")).Expect(HttpStatusCode.OK);

        // Inspection finds a worse condition: Gut with housing damage → 300 × 1.0 × 0.9 = 270 (quote's price, not 250).
        var inspected = (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/inspect",
            new { condition = "Gut", defects = new[] { "HousingDamage" }, batteryHealthPercent = 91, notes = "Kratzer Rückseite" }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal(27000, inspected["inspectedOfferMinor"]!.GetValue<long>());

        Assert.Equal("ankauf.paid_above_offer",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 280m, paymentMethod = "Cash" })).Code);
        var completed = (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash" }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Completed", completed["status"]!.GetValue<string>());
        Assert.NotNull(completed["stockUnitId"]);

        var stock = (await dealer.GetAsync("/api/inventory/stock")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        var row = stock.Single(i => i!["brand"]!.GetValue<string>() == brand)!;
        Assert.Equal(1, row["onHand"]!.GetValue<int>());
        var movements = (await dealer.GetAsync($"/api/inventory/movements?productId={row["productId"]}")).Json["items"]!.AsArray();
        Assert.Equal("UnitReceipt", movements.Single()!["type"]!.GetValue<string>());

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var unit = new NpgsqlCommand($"SELECT purchase_price_minor, tax_scheme FROM inventory.stock_units WHERE imei = '{imei}'", owner);
        await using var reader = await unit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(22000L, reader.GetInt64(0));
        Assert.Equal("Margin25a", reader.GetString(1));
    }

    [Fact]
    public async Task A_device_cannot_be_bought_into_stock_twice_and_minors_cannot_sell()
    {
        var (dealer, key, _, _) = await PricedModelAsync();
        var imei = NewImei();
        var quoteId = (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Gut" }))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        var minor = new
        {
            firstName = "Tim", lastName = "Jung", street = "A 1", postalCode = "10115", city = "Berlin",
            birthDate = DateTime.UtcNow.AddYears(-16).ToString("yyyy-MM-dd"), idDocumentType = "IdCard", idDocumentNumber = "X1",
        };
        Assert.Equal("ankauf.seller_minor", (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId, imei, seller = minor })).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId, imei = "123456789012345", seller = AdultSeller })).Status);

        var intakeId = (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId, imei, seller = AdultSeller }))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        Assert.Equal("ankauf.quote_used", (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId, imei, seller = AdultSeller })).Code);
        (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/inspect", new { condition = "Gut" })).Expect(HttpStatusCode.OK);
        (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 200m, paymentMethod = "BankTransfer" }))
            .Expect(HttpStatusCode.OK);

        // Another shop is offered the same (now stocked) device: refused before any money changes hands.
        var otherShop = await DealerAsync();
        var otherQuoteId = (await otherShop.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Gut" }))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        var refused = await otherShop.PostAsync("/api/ankauf/intakes", new { quoteId = otherQuoteId, imei, seller = AdultSeller });
        Assert.Equal("unit.imei_in_stock", refused.Code);
        // … and already when quoting with that IMEI.
        Assert.Equal("unit.imei_in_stock", (await otherShop.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Gut", imei })).Code);
        // A request without seller data is a clean validation error, not a server error.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await otherShop.PostAsync("/api/ankauf/intakes", new { quoteId = otherQuoteId, imei = NewImei() })).Status);

        // And it cannot see the first shop's intake.
        Assert.Equal(HttpStatusCode.NotFound, (await otherShop.GetAsync($"/api/ankauf/intakes/{intakeId}")).Status);
    }

    [Fact]
    public async Task Ankaufbeleg_is_issued_for_completed_purchases_of_the_own_shop_only()
    {
        var (dealer, key, _, brand) = await PricedModelAsync();
        var imei = NewImei();
        var quoteId = (await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Gut" }))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        var intakeId = (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId, imei, seller = AdultSeller }))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        // Not paid yet → no receipt.
        var early = await dealer.GetAsync($"/api/ankauf/intakes/{intakeId}/beleg");
        Assert.Equal(HttpStatusCode.Conflict, early.Status);
        Assert.Equal("ankauf.not_completed", early.Code);
        (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/inspect", new { condition = "Gut", defects = new[] { "HousingDamage" } }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal("ankauf.not_completed", (await dealer.GetAsync($"/api/ankauf/intakes/{intakeId}/beleg")).Code);
        (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 219.5m, paymentMethod = "Cash" }))
            .Expect(HttpStatusCode.OK);

        var (status, mediaType, fileName, body) = await dealer.GetFileAsync($"/api/ankauf/intakes/{intakeId}/beleg");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/pdf", mediaType);
        Assert.StartsWith("AB-", fileName);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(body, 0, 8));
        var lines = PdfText.Lines(body);
        var text = string.Join("\n", lines);
        Assert.Contains("Ankaufbeleg", lines);
        Assert.Contains("Max Mustermann", lines);
        Assert.Contains(imei, lines);
        Assert.Contains("219,50 €", lines);
        Assert.Contains("Barzahlung", lines);
        Assert.Contains(brand, lines);
        Assert.Contains("Gehäuseschaden", lines);
        Assert.Contains(lines, l => l.StartsWith("ankaufshop ", StringComparison.Ordinal));
        Assert.Contains("Hauptstraße 1, 10115 Berlin", lines); // dealer address from Tenancy, with ß
        Assert.Contains("*****0T47", lines);
        Assert.DoesNotContain("L01X00T47", text);
        Assert.Contains("§ 25a UStG", text.Replace("\n", " "));

        // Another shop cannot fetch it (not revealed), nor can anyone fetch an unknown intake.
        var otherShop = await DealerAsync();
        var foreign = await otherShop.GetAsync($"/api/ankauf/intakes/{intakeId}/beleg");
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("ankauf.intake_not_found", foreign.Code);
        Assert.Equal(HttpStatusCode.NotFound, (await dealer.GetAsync($"/api/ankauf/intakes/{Guid.NewGuid()}/beleg")).Status);
    }
}
