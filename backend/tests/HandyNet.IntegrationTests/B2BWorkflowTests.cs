using System.Net;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class B2BWorkflowTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private static Dictionary<string, string> Key(string key) => new() { ["Idempotency-Key"] = key };

    [Fact]
    public async Task Supplier_import_to_shop_order_to_shipment()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var ean = Scenario.NewEan();
        var mpn = "661-" + Scenario.UniqueSuffix();
        var csv =
            "Artikelnummer;EAN;Herstellernummer;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand\n" +
            $"D-1;{ean};;Apple;iPhone 13 Display;Display;ServicePack;89,90;5\n" +
            $"B-1;;{mpn};Samsung;Galaxy S23 Akku;Akku;Original;24,50;2\n" +
            "X-1;123;;Apple;Kaputt;Display;;10;1\n";

        // Preview is a dry run: nothing is visible on the marketplace yet.
        var preview = (await supplier.UploadImportAsync("preise.csv", csv)).Expect(HttpStatusCode.Created);
        Assert.Equal(3, preview.Json["summary"]!["totalRows"]!.GetValue<int>());
        Assert.Equal(2, preview.Json["summary"]!["validRows"]!.GetValue<int>());
        Assert.Equal(1, preview.Json["summary"]!["errorRows"]!.GetValue<int>());
        Assert.Equal(2, preview.Json["summary"]!["newProducts"]!.GetValue<int>());
        var empty = (await shop.GetAsync($"/api/marketplace/offers?search={ean}")).Expect(HttpStatusCode.OK);
        Assert.Empty(empty.Json["items"]!.AsArray());

        var batchId = preview.Json["id"]!.GetValue<string>();
        var errors = (await supplier.GetAsync($"/api/imports/{batchId}/rows?status=Error")).Expect(HttpStatusCode.OK);
        Assert.Contains("EAN", errors.Json["items"]![0]!["messages"]![0]!.GetValue<string>());

        (await supplier.PostAsync($"/api/imports/{batchId}/commit")).Expect(HttpStatusCode.OK);
        var again = await supplier.PostAsync($"/api/imports/{batchId}/commit");
        Assert.Equal(HttpStatusCode.Conflict, again.Status);

        // Shop finds the offer with live availability.
        var listings = (await shop.GetAsync($"/api/marketplace/offers?search={ean}")).Expect(HttpStatusCode.OK);
        var listing = listings.Json["items"]!.AsArray().Single()!;
        Assert.Equal(5, listing["available"]!.GetValue<int>());
        Assert.Equal(8990, listing["priceMinor"]!.GetValue<long>());
        var offerId = listing["offerId"]!.GetValue<string>();

        // Place order with expected price; 3 × 89.90 = 269.70 net, VAT 51.24, gross 320.94.
        var order = (await shop.PostAsync("/api/orders",
            new { lines = new[] { new { offerId, quantity = 3, expectedUnitPriceMinor = 8990 } }, buyerReference = "PO-4711" },
            Key("order-1"))).Expect(HttpStatusCode.Created);
        Assert.Equal(26970, order.Json["netTotalMinor"]!.GetValue<long>());
        Assert.Equal(5124, order.Json["vatTotalMinor"]!.GetValue<long>());
        Assert.Equal(32094, order.Json["grossTotalMinor"]!.GetValue<long>());
        var orderId = order.Json["id"]!.GetValue<string>();

        // Retrying the same request (e.g. after a timeout) does not create a second order.
        var replay = (await shop.PostAsync("/api/orders",
                new { lines = new[] { new { offerId, quantity = 3, expectedUnitPriceMinor = 8990 } }, buyerReference = "PO-4711" }, Key("order-1")))
            .Expect(HttpStatusCode.OK);
        Assert.Equal(orderId, replay.Json["id"]!.GetValue<string>());

        // Only 2 left available.
        var tooMuch = await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 3 } } }, Key("order-2"));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.Status);
        Assert.Equal("order.insufficient_stock", tooMuch.Code);

        // Seller workflow; shipping consumes the reservation.
        var sales = (await supplier.GetAsync("/api/orders?role=sales")).Expect(HttpStatusCode.OK);
        Assert.Contains(sales.Json["items"]!.AsArray(), o => o!["id"]!.GetValue<string>() == orderId);
        Assert.Equal(HttpStatusCode.Conflict, (await supplier.PostAsync($"/api/orders/{orderId}/ship")).Status); // must confirm first
        (await supplier.PostAsync($"/api/orders/{orderId}/confirm")).Expect(HttpStatusCode.OK);
        var shipped = (await supplier.PostAsync($"/api/orders/{orderId}/ship")).Expect(HttpStatusCode.OK);
        Assert.Equal("Shipped", shipped.Json["status"]!.GetValue<string>());

        var stock = (await supplier.GetAsync("/api/inventory/stock")).Expect(HttpStatusCode.OK);
        var level = stock.Json["items"]!.AsArray().Single(i => i!["ean"]?.GetValue<string>() == ean)!;
        Assert.Equal(2, level["onHand"]!.GetValue<int>());
        Assert.Equal(0, level["reserved"]!.GetValue<int>());

        var movements = (await supplier.GetAsync($"/api/inventory/movements?productId={level["productId"]}")).Expect(HttpStatusCode.OK);
        Assert.Equal(["Shipment", "ImportSync"], movements.Json["items"]!.AsArray().Select(m => m!["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Cancelling_releases_reserved_stock()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 2);

        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 2 } } }, Key("c-1")))
            .Expect(HttpStatusCode.Created);
        var listing = (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Expect(HttpStatusCode.OK);
        Assert.Equal(0, listing.Json["available"]!.GetValue<int>());

        (await shop.PostAsync($"/api/orders/{order.Json["id"]}/cancel", new { reason = "Falsch bestellt" })).Expect(HttpStatusCode.OK);
        listing = (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Expect(HttpStatusCode.OK);
        Assert.Equal(2, listing.Json["available"]!.GetValue<int>());

        // Cancelling twice is rejected and does not release stock twice.
        Assert.Equal(HttpStatusCode.Conflict, (await shop.PostAsync($"/api/orders/{order.Json["id"]}/cancel")).Status);
        listing = (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Expect(HttpStatusCode.OK);
        Assert.Equal(2, listing.Json["available"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_is_rejected()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 10);

        (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 1 } } }, Key("k-1"))).Expect(HttpStatusCode.Created);
        var reused = await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 2 } } }, Key("k-1"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.Status);
        Assert.Equal("order.idempotency_key_reused", reused.Code);
        var listing = (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Json;
        Assert.Equal(9, listing["available"]!.GetValue<int>()); // second request reserved nothing
    }

    [Fact]
    public async Task Conflicts_carry_machine_readable_details()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 2, price: "50,00");

        var price = await shop.PostAsync("/api/orders",
            new { lines = new[] { new { offerId, quantity = 1, expectedUnitPriceMinor = 4500 } } }, Key("d-1"));
        Assert.Equal(offerId.ToString(), price.Json["offerId"]!.GetValue<string>());
        Assert.Equal(5000, price.Json["currentUnitPriceMinor"]!.GetValue<long>());

        var stock = await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 3 } } }, Key("d-2"));
        Assert.Equal("order.insufficient_stock", stock.Code);
        Assert.Equal(2, stock.Json["availableQuantity"]!.GetValue<int>());
        Assert.Equal(3, stock.Json["requestedQuantity"]!.GetValue<int>());
    }

    [Fact]
    public async Task Stock_adjustments_report_negative_stock_and_record_the_chosen_type()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        await _scenario.PublishOfferAsync(supplier, shop, quantity: 2);
        var level = (await supplier.GetAsync("/api/inventory/stock")).Json["items"]![0]!;
        var target = new { warehouseId = level["warehouseId"]!.GetValue<string>(), productId = level["productId"]!.GetValue<string>() };

        var negative = await supplier.PostAsync("/api/inventory/adjustments",
            new { target.warehouseId, target.productId, delta = -5, reason = "Inventur" });
        Assert.Equal("stock.negative", negative.Code);

        (await supplier.PostAsync("/api/inventory/adjustments",
            new { target.warehouseId, target.productId, delta = 1, reason = "Zählung" })).Expect(HttpStatusCode.OK);
        (await supplier.PostAsync("/api/inventory/adjustments",
            new { target.warehouseId, target.productId, delta = 3, reason = "Lieferung", type = "Receipt" })).Expect(HttpStatusCode.OK);

        var types = (await supplier.GetAsync($"/api/inventory/movements?productId={target.productId}")).Json["items"]!.AsArray()
            .Select(m => m!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["Receipt", "Adjustment", "ImportSync"], types);
    }

    [Fact]
    public async Task Changed_price_is_not_silently_accepted()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5, price: "50,00");

        var response = await shop.PostAsync("/api/orders",
            new { lines = new[] { new { offerId, quantity = 1, expectedUnitPriceMinor = 4500 } } }, Key("p-1"));
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("order.price_changed", response.Code);
    }

    [Fact]
    public async Task Reimport_updates_price_and_stock_and_rejects_quantity_below_reservations()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var ean = Scenario.NewEan();
        const string header = "Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Preis;Bestand\n";
        var first = (await supplier.UploadImportAsync("a.csv", header + $"S-1;{ean};Apple;Akku;Battery;10,00;10\n")).Expect(HttpStatusCode.Created);
        (await supplier.PostAsync($"/api/imports/{first.Json["id"]}/commit")).Expect(HttpStatusCode.OK);

        var offerId = (await shop.GetAsync($"/api/marketplace/offers?search={ean}")).Json["items"]![0]!["offerId"]!.GetValue<string>();
        (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 4 } } }, Key("r-1"))).Expect(HttpStatusCode.Created);

        var tooLow = (await supplier.UploadImportAsync("b.csv", header + $"S-1;{ean};Apple;Akku;Battery;10,00;3\n")).Expect(HttpStatusCode.Created);
        Assert.Equal(1, tooLow.Json["summary"]!["errorRows"]!.GetValue<int>());

        var update = (await supplier.UploadImportAsync("c.csv", header + $"S-1;{ean};Apple;Akku;Battery;15,00;8\n")).Expect(HttpStatusCode.Created);
        Assert.Equal(1, update.Json["summary"]!["updatedOffers"]!.GetValue<int>());
        Assert.Equal(0, update.Json["summary"]!["newProducts"]!.GetValue<int>());
        var row = (await supplier.GetAsync($"/api/imports/{update.Json["id"]}/rows")).Json["items"]![0]!;
        Assert.Equal(1000, row["oldPriceMinor"]!.GetValue<long>());
        Assert.Contains(row["messages"]!.AsArray(), m => m!.GetValue<string>().Contains("50%"));
        (await supplier.PostAsync($"/api/imports/{update.Json["id"]}/commit")).Expect(HttpStatusCode.OK);

        var listing = (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Json;
        Assert.Equal(1500, listing["priceMinor"]!.GetValue<long>());
        Assert.Equal(4, listing["available"]!.GetValue<int>()); // 8 on hand − 4 reserved
    }

    [Fact]
    public async Task Same_ean_with_different_quality_from_another_supplier_is_flagged_not_merged()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var other = await _scenario.RegisterAsync("teilehandel", wholesaler: true);
        await _scenario.ApproveAsync(other, "ProductImport", "Inventory", "B2BSales");
        var (_, ean) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);

        var csv = $"Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand\nX;{ean};Apple;Display Nachbau;Display;Aftermarket;19,90;5\n";
        var preview = (await other.UploadImportAsync("copy.csv", csv)).Expect(HttpStatusCode.Created);
        Assert.Equal(1, preview.Json["summary"]!["conflictRows"]!.GetValue<int>());
        Assert.Equal(0, preview.Json["summary"]!["validRows"]!.GetValue<int>());
    }

    [Fact]
    public async Task Import_with_explicit_mapping_and_unknown_column_is_rejected()
    {
        var (supplier, _) = await _scenario.SupplierAndShopAsync();
        var response = await supplier.UploadImportAsync("x.csv", "A;B\n1;2\n",
            mapping: new Dictionary<string, string> { ["sku"] = "A", ["brand"] = "Nope", ["name"] = "B", ["price"] = "B", ["ean"] = "A" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
        Assert.Equal("import.mapping_unknown_column", response.Code);
    }
}
