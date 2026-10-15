using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class PosTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private static Dictionary<string, string> Key(string? key = null) => new() { ["Idempotency-Key"] = key ?? Guid.NewGuid().ToString("N") };

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

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

    private async Task<ApiClient> PosShopAsync(params string[] extraModules)
    {
        var shop = await _scenario.RegisterAsync("kassenshop", wholesaler: false);
        await _scenario.ApproveAsync(shop, ["Pos", "Inventory", "ProductImport", .. extraModules]);
        return shop;
    }

    private static async Task<Guid> DefaultWarehouseAsync(ApiClient shop)
    {
        var warehouses = (await shop.GetAsync("/api/inventory/warehouses")).Expect(HttpStatusCode.OK).Json.AsArray();
        if (warehouses.Count > 0)
            return Id(warehouses.First(w => w!["isDefault"]!.GetValue<bool>())!);
        return Id((await shop.PostAsync("/api/inventory/warehouses", new { code = "MAIN", name = "Hauptlager" })).Expect(HttpStatusCode.Created).Json);
    }

    /// <summary>Creates a catalog accessory with an EAN and books <paramref name="quantity"/> into the default warehouse.</summary>
    private static async Task<(Guid ProductId, string Ean)> StockProductAsync(ApiClient shop, int quantity)
    {
        var ean = Scenario.NewEan();
        var product = (await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = "Ladekabel USB-C " + Scenario.UniqueSuffix(), category = "Accessory", quality = "Aftermarket", ean,
        })).Expect(HttpStatusCode.Created).Json;
        var productId = Id(product);
        var warehouseId = await DefaultWarehouseAsync(shop);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId, delta = quantity, reason = "Wareneingang", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        return (productId, ean);
    }

    /// <summary>Stocks a used device bought from a private person (§25a) directly through the Inventory contract, like an Ankauf intake does.</summary>
    private async Task<Guid> StockDeviceAsync(ApiClient shop, string imei, long purchasePriceMinor)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var organizationId = shop.OrganizationId!.Value;
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(organizationId);
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalog>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        var product = await catalog.FindOrStageDeviceAsync("TestPhone", "Modell " + Scenario.UniqueSuffix(), "128 GB", organizationId, CancellationToken.None);
        var warehouseId = await inventory.GetOrStageDefaultWarehouseAsync(CancellationToken.None);
        var unit = await inventory.StageReceiveUnitAsync(warehouseId, product.Value.Id, imei, purchasePriceMinor, TaxScheme.Margin25a, "test:direct",
            CancellationToken.None);
        Assert.True(unit.IsSuccess, unit.Error?.Message);
        await scope.ServiceProvider.GetRequiredService<HandyNetDbContext>().SaveChangesAsync();
        return product.Value.Id;
    }

    private static async Task<Guid> RegisterWithSessionAsync(ApiClient shop, string name, long openingCashMinor = 100_00)
    {
        var register = Id((await shop.PostAsync("/api/pos/registers", new { name })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor })).Expect(HttpStatusCode.Created);
        return register;
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<JsonNode> StockRowAsync(ApiClient shop, Guid productId) =>
        (await shop.GetAsync("/api/inventory/stock?pageSize=200")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
        .Single(i => Id(i!, "productId") == productId)!;

    [Fact]
    public async Task Sale_storno_and_daily_closing_with_gapless_numbers_stock_and_vat()
    {
        var shop = await PosShopAsync();
        var (cable, ean) = await StockProductAsync(shop, 5);
        var imei = NewImei();
        var device = await StockDeviceAsync(shop, imei, purchasePriceMinor: 220_00);

        var register = Id((await shop.PostAsync("/api/pos/registers", new { name = "Kasse 1" })).Expect(HttpStatusCode.Created).Json);
        object SaleBody(long cash) => new
        {
            registerId = register,
            lines = new object[]
            {
                new { productId = cable, quantity = 2, unitPriceMinor = 10_00, discountMinor = 1_00 },
                new { imei, quantity = 1, unitPriceMinor = 300_00 },
            },
            payments = new[] { new { method = "Cash", amountMinor = cash } },
        };

        // No open session: refused, and no receipt number is used up.
        Assert.Equal("pos.no_open_session", (await shop.PostAsync("/api/pos/sales", SaleBody(400_00), Key())).Code);
        (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 100_00 })).Expect(HttpStatusCode.Created);
        Assert.Equal("pos.session_already_open",
            (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 0 })).Code);

        // Lookup by EAN (own stock) and by IMEI (own in-stock device).
        var byEan = (await shop.GetAsync($"/api/pos/lookup?code={ean}&registerId={register}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Product", 5), (byEan["kind"]!.GetValue<string>(), byEan["available"]!.GetValue<int>()));
        var byImei = (await shop.GetAsync($"/api/pos/lookup?code={imei}&registerId={register}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Unit", device, "Margin25a"), (byImei["kind"]!.GetValue<string>(), Id(byImei, "productId"), byImei["taxTreatment"]!.GetValue<string>()));

        // Sale 1: 2 cables (19.00 incl. 3.03 VAT) + used phone under §25a (300.00, bought for 220.00 → 12.77 VAT on the margin).
        var first = (await shop.PostAsync("/api/pos/sales", SaleBody(400_00), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(1, first["number"]!.GetValue<long>());
        Assert.Equal(319_00, first["totalGrossMinor"]!.GetValue<long>());
        Assert.Equal(81_00, first["changeMinor"]!.GetValue<long>());
        var vat = first["vat"]!.AsArray().Single()!; // §25a VAT is never printed
        Assert.Equal((19_00L, 3_03L), (vat["grossMinor"]!.GetValue<long>(), vat["vatMinor"]!.GetValue<long>()));
        Assert.Null(first["lines"]![1]!["vatMinor"]);
        var document = first["document"]!;
        Assert.Equal("Kasse 1", document["registerName"]!.GetValue<string>());
        Assert.Contains("GmbH", document["seller"]!["legalName"]!.GetValue<string>());
        Assert.Contains(document["notes"]!.AsArray(), n => n!.GetValue<string>() == "TSE nicht angebunden – Testbetrieb");
        Assert.Equal("NotConnected", first["fiscal"]!["status"]!.GetValue<string>());
        Assert.Null(first["fiscal"]!["signature"]);
        Assert.Equal(12_77L, await AsOwnerAsync<long>($"SELECT vat_minor FROM pos.receipt_lines WHERE receipt_id = '{first["id"]}' AND line_number = 2"));

        // Failed sales (not enough stock, device already sold) do not consume a number either.
        Assert.Equal("stock.insufficient", (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 4, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Card", amountMinor = 40_00 } },
        }, Key())).Code);
        Assert.Equal("unit.not_available", (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { imei, quantity = 1, unitPriceMinor = 300_00 } },
            payments = new[] { new { method = "Card", amountMinor = 300_00 } },
        }, Key())).Code);

        var second = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Card", amountMinor = 10_00 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(2, second["number"]!.GetValue<long>());

        Assert.Equal(2, (await StockRowAsync(shop, cable))["onHand"]!.GetValue<int>());
        Assert.Equal("Sold", await AsOwnerAsync<string>($"SELECT status FROM inventory.stock_units WHERE imei = '{imei}'"));
        Assert.Equal("pos.code_not_found", (await shop.GetAsync($"/api/pos/lookup?code={imei}")).Code);

        // Storno of sale 1: a new negative receipt (number 3); stock and the device come back.
        var storno = (await shop.PostAsync($"/api/pos/sales/{first["id"]}/storno", new { reason = "Kunde hat es sich anders überlegt" }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal((3L, "Storno", -319_00L), (storno["number"]!.GetValue<long>(), storno["kind"]!.GetValue<string>(), storno["totalGrossMinor"]!.GetValue<long>()));
        Assert.Equal(1, storno["referencedReceiptNumber"]!.GetValue<long>());
        Assert.Equal(4, (await StockRowAsync(shop, cable))["onHand"]!.GetValue<int>());
        Assert.Equal("InStock", await AsOwnerAsync<string>($"SELECT status FROM inventory.stock_units WHERE imei = '{imei}'"));
        (await shop.GetAsync($"/api/pos/lookup?code={imei}")).Expect(HttpStatusCode.OK);
        Assert.Equal("pos.already_cancelled", (await shop.PostAsync($"/api/pos/sales/{first["id"]}/storno", new { reason = "nochmal" })).Code);
        Assert.Equal("pos.already_cancelled", (await shop.PostAsync($"/api/pos/sales/{first["id"]}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "x" }, Key())).Code);
        var original = (await shop.GetAsync($"/api/pos/sales/{first["id"]}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(storno), Id(original, "cancelledByReceiptId"));

        var movements = (await shop.GetAsync($"/api/inventory/movements?productId={cable}")).Json["items"]!.AsArray().Select(m => m!["type"]!.GetValue<string>());
        Assert.Equal(["Return", "Sale", "Sale", "Receipt"], movements);

        // Cash movements, then the daily closing.
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        var sessionId = Id(session);
        (await shop.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Deposit", amountMinor = 20_00, reason = "Wechselgeld" }))
            .Expect(HttpStatusCode.Created);
        (await shop.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Withdrawal", amountMinor = 30_00, reason = "Bank" }))
            .Expect(HttpStatusCode.Created);
        Assert.Equal("pos.insufficient_cash",
            (await shop.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Withdrawal", amountMinor = 1_000_00, reason = "Bank" })).Code);

        // 100.00 opening + 319.00 cash sale − 319.00 storno + 20.00 − 30.00 = 90.00
        var closed = (await shop.PostAsync($"/api/pos/sessions/{sessionId}/close", new { countedCashMinor = 89_50, note = "50 ct fehlen" }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Closed", closed["status"]!.GetValue<string>());
        Assert.Equal((90_00L, 89_50L, -50L), (closed["expectedCashMinor"]!.GetValue<long>(), closed["countedCashMinor"]!.GetValue<long>(),
            closed["differenceMinor"]!.GetValue<long>()));
        var summary = closed["summary"]!;
        Assert.Equal((3, 2, 1), (summary["receiptCount"]!.GetValue<int>(), summary["saleCount"]!.GetValue<int>(), summary["stornoCount"]!.GetValue<int>()));
        Assert.Equal((1L, 3L), (summary["firstReceiptNumber"]!.GetValue<long>(), summary["lastReceiptNumber"]!.GetValue<long>()));
        long Payment(string method) => summary["byPaymentMethod"]!.AsArray().Single(p => p!["method"]!.GetValue<string>() == method)!["amountMinor"]!.GetValue<long>();
        Assert.Equal((0L, 10_00L), (Payment("Cash"), Payment("Card")));
        JsonNode Tax(string treatment) => summary["byTaxTreatment"]!.AsArray().Single(t => t!["treatment"]!.GetValue<string>() == treatment)!;
        Assert.Equal((10_00L, 1_60L), (Tax("Standard19")["grossMinor"]!.GetValue<long>(), Tax("Standard19")["vatMinor"]!.GetValue<long>()));
        Assert.Equal((0L, 0L), (Tax("Margin25a")["grossMinor"]!.GetValue<long>(), Tax("Margin25a")["vatMinor"]!.GetValue<long>()));
        Assert.Equal(2, closed["movements"]!.AsArray().Count);

        // Closed is final.
        Assert.Equal("pos.session_closed", (await shop.PostAsync($"/api/pos/sessions/{sessionId}/close", new { countedCashMinor = 0 })).Code);
        Assert.Equal("pos.session_closed",
            (await shop.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Deposit", amountMinor = 1_00, reason = "x" })).Code);
        Assert.Equal("pos.no_open_session", (await shop.PostAsync("/api/pos/sales", SaleBody(400_00), Key())).Code);
        Assert.Equal("pos.no_open_session", (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Code);

        var numbers = (await shop.GetAsync($"/api/pos/sales?registerId={register}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Select(r => r!["number"]!.GetValue<long>()).Order();
        Assert.Equal([1L, 2L, 3L], numbers);
        Assert.Equal(4, (await shop.GetAsync($"/api/pos/registers/{register}")).Json["nextReceiptNumber"]!.GetValue<long>());
    }

    [Fact]
    public async Task Partial_returns_refund_proportionally_and_never_more_than_sold()
    {
        var shop = await PosShopAsync();
        var (cable, _) = await StockProductAsync(shop, 3);
        var register = await RegisterWithSessionAsync(shop, "Kasse R", openingCashMinor: 50_00);
        var sale = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 3, unitPriceMinor = 10_00, discountMinor = 1_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 30_00 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;

        var returnKey = Key();
        object ReturnBody(int quantity) => new { lines = new[] { new { lineNumber = 1, quantity } }, refundMethod = "Cash", reason = "defekt" };
        var first = (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/returns", ReturnBody(1), returnKey)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("Return", -9_67L), (first["kind"]!.GetValue<string>(), first["totalGrossMinor"]!.GetValue<long>()));
        // Retrying the same return is idempotent.
        Assert.Equal(Id(first), Id((await shop.PostAsync($"/api/pos/sales/{sale["id"]}/returns", ReturnBody(1), returnKey)).Expect(HttpStatusCode.OK).Json));

        Assert.Equal("pos.return_exceeds_sold", (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/returns", ReturnBody(3), Key())).Code);
        var rest = (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/returns", ReturnBody(2), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(-19_33L, rest["totalGrossMinor"]!.GetValue<long>()); // 9.67 + 19.33 = 29.00 exactly
        Assert.Equal("pos.has_returns", (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/storno", new { reason = "x" })).Code);
        Assert.Equal(3, (await StockRowAsync(shop, cable))["onHand"]!.GetValue<int>());
        var original = (await shop.GetAsync($"/api/pos/sales/{sale["id"]}")).Json;
        Assert.Equal(3, original["lines"]![0]!["returnedQuantity"]!.GetValue<int>());
        Assert.Equal(50_00, (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Json["expectedCashMinor"]!.GetValue<long>());
    }

    [Fact]
    public async Task Stock_reserved_by_a_B2B_order_is_never_sold_over_the_counter()
    {
        var supplier = await _scenario.RegisterAsync("grosshandel", wholesaler: true);
        var shop = await _scenario.RegisterAsync("handyshop", wholesaler: false);
        await _scenario.ApproveAsync(supplier, "ProductImport", "Inventory", "B2BSales", "Pos");
        await _scenario.ApproveAsync(shop, "B2BPurchasing", "Inventory");
        var (offerId, ean) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 3);
        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 2 } } }, Key())).Expect(HttpStatusCode.Created).Json;

        var register = await RegisterWithSessionAsync(supplier, "Ladenkasse");
        var lookup = (await supplier.GetAsync($"/api/pos/lookup?code={ean}&registerId={register}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((3, 2, 1), (lookup["onHand"]!.GetValue<int>(), lookup["reserved"]!.GetValue<int>(), lookup["available"]!.GetValue<int>()));
        var productId = Id(lookup, "productId");

        object Sale(int quantity) => new
        {
            registerId = register, lines = new[] { new { productId, quantity, unitPriceMinor = 99_00 } },
            payments = new[] { new { method = "Card", amountMinor = 99_00L * quantity } },
        };
        var refused = await supplier.PostAsync("/api/pos/sales", Sale(2), Key());
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal(("stock.insufficient", 1), (refused.Code, refused.Json["availableQuantity"]!.GetValue<int>()));
        (await supplier.PostAsync("/api/pos/sales", Sale(1), Key())).Expect(HttpStatusCode.Created);
        Assert.Equal("stock.insufficient", (await supplier.PostAsync("/api/pos/sales", Sale(1), Key())).Code);

        // The B2B order still gets its goods.
        (await supplier.PostAsync($"/api/orders/{order["id"]}/confirm")).Expect(HttpStatusCode.OK);
        (await supplier.PostAsync($"/api/orders/{order["id"]}/ship")).Expect(HttpStatusCode.OK);
        var level = await StockRowAsync(supplier, productId);
        Assert.Equal((0, 0), (level["onHand"]!.GetValue<int>(), level["reserved"]!.GetValue<int>()));
    }

    [Fact]
    public async Task The_same_device_cannot_be_sold_twice_even_concurrently()
    {
        var shop = await PosShopAsync();
        var imei = NewImei();
        await StockDeviceAsync(shop, imei, 100_00);
        var registers = new[] { await RegisterWithSessionAsync(shop, "Kasse A"), await RegisterWithSessionAsync(shop, "Kasse B") };

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => shop.PostAsync("/api/pos/sales", new
        {
            registerId = registers[i % 2], lines = new[] { new { imei, quantity = 1, unitPriceMinor = 150_00 } },
            payments = new[] { new { method = "Card", amountMinor = 150_00 } },
        }, Key())));

        Assert.Single(attempts, a => a.Status == HttpStatusCode.Created);
        Assert.All(attempts.Where(a => a.Status != HttpStatusCode.Created), a =>
        {
            Assert.Equal(HttpStatusCode.Conflict, a.Status);
            Assert.Contains(a.Code, new[] { "unit.not_available", "pos.stock_changed" });
        });
        Assert.Equal(1L, await AsOwnerAsync<long>($"SELECT count(*) FROM pos.receipt_lines WHERE imei = '{imei}'"));
        Assert.Equal("Sold", await AsOwnerAsync<string>($"SELECT status FROM inventory.stock_units WHERE imei = '{imei}'"));

        // Failed attempts gave their receipt numbers back: exactly one register moved, to 2.
        var next = new List<long>();
        foreach (var register in registers)
            next.Add((await shop.GetAsync($"/api/pos/registers/{register}")).Json["nextReceiptNumber"]!.GetValue<long>());
        Assert.Equal([1L, 2L], next.Order());
    }

    [Fact]
    public async Task Retrying_a_sale_with_the_same_idempotency_key_returns_the_original_receipt()
    {
        var shop = await PosShopAsync();
        var (cable, _) = await StockProductAsync(shop, 10);
        var register = await RegisterWithSessionAsync(shop, "Kasse 1");
        var body = new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 1, unitPriceMinor = 12_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 20_00 } },
        };

        var key = Key();
        var created = (await shop.PostAsync("/api/pos/sales", body, key)).Expect(HttpStatusCode.Created).Json;
        var replay = (await shop.PostAsync("/api/pos/sales", body, key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((Id(created), 1L), (Id(replay), replay["number"]!.GetValue<long>()));
        var differentBody = new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 2, unitPriceMinor = 12_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 30_00 } },
        };
        Assert.Equal("pos.idempotency_key_reused", (await shop.PostAsync("/api/pos/sales", differentBody, key)).Code);
        Assert.Equal("pos.idempotency_key", (await shop.PostAsync("/api/pos/sales", body)).Code);

        // Concurrent duplicates (double click, network retry): one receipt, all callers get it.
        var concurrentKey = Key();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => shop.PostAsync("/api/pos/sales", body, concurrentKey)));
        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Body?.ToJsonString()));
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.Single(results.Select(r => Id(r.Json)).Distinct());

        Assert.Equal(8, (await StockRowAsync(shop, cable))["onHand"]!.GetValue<int>()); // 10 − 1 − 1
        Assert.Equal(3, (await shop.GetAsync($"/api/pos/registers/{register}")).Json["nextReceiptNumber"]!.GetValue<long>());
    }

    [Fact]
    public async Task Pos_data_is_isolated_between_tenants_and_requires_the_module()
    {
        var shopA = await PosShopAsync();
        var (cable, _) = await StockProductAsync(shopA, 2);
        var register = await RegisterWithSessionAsync(shopA, "Kasse 1");
        var sale = (await shopA.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 1, unitPriceMinor = 5_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 5_00 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;

        var shopB = await PosShopAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await shopB.GetAsync($"/api/pos/sales/{sale["id"]}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await shopB.GetAsync($"/api/pos/registers/{register}")).Status);
        Assert.Empty((await shopB.GetAsync("/api/pos/registers")).Json.AsArray());
        Assert.Equal(0, (await shopB.GetAsync("/api/pos/sales")).Json["totalCount"]!.GetValue<int>());
        Assert.Equal("pos.register_not_found", (await shopB.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 1, unitPriceMinor = 5_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 5_00 } },
        }, Key())).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await shopB.PostAsync($"/api/pos/sales/{sale["id"]}/storno", new { reason = "x" })).Status);

        var withoutModule = await _scenario.RegisterAsync("ohnekasse", wholesaler: false);
        await _scenario.ApproveAsync(withoutModule, "Inventory");
        Assert.Equal("module.disabled", (await withoutModule.GetAsync("/api/pos/registers")).Code);

        // Database level: the app role sees nothing of A when acting for B or for no tenant.
        string[] tables = ["registers", "cash_sessions", "cash_movements", "receipts", "receipt_lines", "receipt_payments"];
        foreach (var organization in new Guid?[] { shopB.OrganizationId, null })
        {
            await using var app = new NpgsqlConnection(fixture.AppConnectionString);
            await app.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", app))
            {
                set.Parameters.AddWithValue("org", organization?.ToString() ?? string.Empty);
                await set.ExecuteNonQueryAsync();
            }
            foreach (var table in tables)
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM pos.{table} WHERE organization_id = '{shopA.OrganizationId}'", app);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            }
            if (organization is not null)
            {
                await using var insert = new NpgsqlCommand(
                    $"INSERT INTO pos.registers (id, organization_id, name, warehouse_id, next_receipt_number, is_active, created_at) "
                    + $"SELECT gen_random_uuid(), '{shopA.OrganizationId}', 'X', '{Guid.NewGuid()}', 1, true, now()", app);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
            }
        }
    }

    [Fact]
    public async Task Receipts_are_immutable_and_numbers_cannot_be_skipped()
    {
        var shop = await PosShopAsync();
        var (cable, _) = await StockProductAsync(shop, 2);
        var register = await RegisterWithSessionAsync(shop, "Kasse 1");
        var sale = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = cable, quantity = 1, unitPriceMinor = 5_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 5_00 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        var sessionId = Id(sale, "sessionId");
        (await shop.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Deposit", amountMinor = 1_00, reason = "x" }))
            .Expect(HttpStatusCode.Created);
        (await shop.PostAsync($"/api/pos/sessions/{sessionId}/close", new { countedCashMinor = 106_00 })).Expect(HttpStatusCode.OK);

        // Not even the owner (migration role) can change or delete receipts, their lines, payments, cash movements or a closed session.
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        foreach (var sql in new[]
                 {
                     $"UPDATE pos.receipts SET total_gross_minor = 1 WHERE id = '{sale["id"]}'",
                     $"DELETE FROM pos.receipts WHERE id = '{sale["id"]}'",
                     $"UPDATE pos.receipt_lines SET unit_price_minor = 1 WHERE receipt_id = '{sale["id"]}'",
                     $"DELETE FROM pos.receipt_payments WHERE receipt_id = '{sale["id"]}'",
                     $"UPDATE pos.cash_movements SET amount_minor = 999 WHERE session_id = '{sessionId}'",
                     $"UPDATE pos.cash_sessions SET counted_cash_minor = 10500, difference_minor = -100 WHERE id = '{sessionId}'",
                     "TRUNCATE pos.receipt_payments",
                 })
        {
            await using var command = new NpgsqlCommand(sql, owner);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState == "42501", $"{sql}: {ex.SqlState} {ex.MessageText}");
        }

        // The application role has no UPDATE/DELETE grant on receipts at all.
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_org', '{shop.OrganizationId}', false)", app))
            await set.ExecuteNonQueryAsync();
        await using (var update = new NpgsqlCommand($"UPDATE pos.receipts SET reason = 'x' WHERE id = '{sale["id"]}'", app))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);

        // Gapless numbering is a database guarantee: skipping a number fails at once, allocating one without a receipt fails at commit.
        await using (var skip = new NpgsqlCommand($"UPDATE pos.registers SET next_receipt_number = next_receipt_number + 2 WHERE id = '{register}'", app))
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => skip.ExecuteNonQueryAsync())).SqlState);
        await using (var tx = await app.BeginTransactionAsync())
        {
            await using var allocate = new NpgsqlCommand($"UPDATE pos.registers SET next_receipt_number = next_receipt_number + 1 WHERE id = '{register}'", app, tx);
            Assert.Equal(1, await allocate.ExecuteNonQueryAsync());
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }
        Assert.Equal(2, (await shop.GetAsync($"/api/pos/registers/{register}")).Json["nextReceiptNumber"]!.GetValue<long>());
    }

    [Fact]
    public async Task Production_refuses_to_start_with_the_disabled_fiscal_signer()
    {
        WebApplicationFactory<Program> Production(string? signer) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Production");
            web.UseSetting("ConnectionStrings:HandyNet", fixture.AppConnectionString);
            web.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            if (signer is not null)
                web.UseSetting("Pos:FiscalSigner", signer);
            // Do not run background workers against the shared test database.
            web.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

        await using (var withTestSigner = Production("Disabled"))
        {
            var ex = Assert.ThrowsAny<Exception>(() => withTestSigner.Server);
            Assert.Contains("Disabled fiscal signer", ex.ToString());
        }

        // Control: without a signer the API starts in Production (POS sales are then refused with pos.fiscal_signer_missing).
        await using var withoutSigner = Production(null);
        var client = withoutSigner.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
