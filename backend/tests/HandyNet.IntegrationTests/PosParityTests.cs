using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>Odoo-parity POS features: product grid + prices, per-cashier rights, customers, line notes, order history, reprints, cash counts, reports.</summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosParityTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);
    private const string StaffPassword = "Kassierer-Passwort-1";

    private static Dictionary<string, string> Key() => new() { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") };

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    private static JsonArray Items(ApiResponse response) => response.Expect(HttpStatusCode.OK).Json["items"]!.AsArray();

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

    private static async Task<Guid> StockProductAsync(ApiClient shop, int quantity, string name, string category = "Accessory", string? ean = null)
    {
        var product = (await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = $"{name} {Scenario.UniqueSuffix()}", category, quality = "Aftermarket", ean = ean ?? Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json;
        var productId = Id(product);
        var warehouseId = await DefaultWarehouseAsync(shop);
        if (quantity > 0)
            (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId, delta = quantity, reason = "Wareneingang", type = "Receipt" }))
                .Expect(HttpStatusCode.OK);
        return productId;
    }

    private async Task<(Guid ProductId, string Imei)> StockDeviceAsync(ApiClient shop)
    {
        var imei = NewImei();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var organizationId = shop.OrganizationId!.Value;
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(organizationId);
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalog>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        var product = await catalog.FindOrStageDeviceAsync("TestPhone", "Modell " + Scenario.UniqueSuffix(), "128 GB", organizationId, CancellationToken.None);
        var warehouseId = await inventory.GetOrStageDefaultWarehouseAsync(CancellationToken.None);
        var unit = await inventory.StageReceiveUnitAsync(warehouseId, product.Value.Id, imei, 100_00, TaxScheme.Margin25a, "test:direct", CancellationToken.None);
        Assert.True(unit.IsSuccess, unit.Error?.Message);
        await scope.ServiceProvider.GetRequiredService<HandyNetDbContext>().SaveChangesAsync();
        return (product.Value.Id, imei);
    }

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

    private static async Task<Guid> RegisterAsync(ApiClient shop, string name = "Kasse 1") =>
        Id((await shop.PostAsync("/api/pos/registers", new { name })).Expect(HttpStatusCode.Created).Json);

    private static async Task<Guid> OpenSessionAsync(ApiClient client, Guid register, long openingCashMinor = 100_00) =>
        Id((await client.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor })).Expect(HttpStatusCode.Created).Json);

    private async Task<(ApiClient Staff, Guid UserId)> StaffAsync(ApiClient owner, string role = "Staff")
    {
        var email = $"kasse-{Scenario.UniqueSuffix()}@test.de";
        var userId = Id((await owner.PostAsync("/api/organization/staff", new { email, displayName = "Kassierer " + role, password = StaffPassword, role }))
            .Expect(HttpStatusCode.OK).Json, "userId");
        var staff = fixture.CreateClient();
        await staff.LoginAsync(email, StaffPassword);
        staff.OrganizationId = owner.OrganizationId;
        return (staff, userId);
    }

    private static object Sale(Guid register, Guid product, long price, long discount = 0, string method = "Cash", long? paid = null, Guid? customerId = null,
        string? note = null, int quantity = 1) => new
    {
        registerId = register,
        customerId,
        lines = new[] { new { productId = product, quantity, unitPriceMinor = price, discountMinor = discount, note } },
        payments = new[] { new { method, amountMinor = paid ?? price * quantity - discount } },
    };

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- 1. product grid and prices ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Product_grid_lists_own_stock_with_category_search_paging_serialized_flag_and_till_prices()
    {
        var shop = await PosShopAsync();
        var marker = Scenario.UniqueSuffix();
        var cable = await StockProductAsync(shop, 5, $"Kabel{marker}");
        var display = await StockProductAsync(shop, 2, $"Display{marker}", category: "Display");
        var soldOut = await StockProductAsync(shop, 0, $"Leer{marker}");
        var warehouse = await DefaultWarehouseAsync(shop);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId = warehouse, productId = soldOut, delta = 1, reason = "x", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId = warehouse, productId = soldOut, delta = -1, reason = "x", type = "Adjustment" }))
            .Expect(HttpStatusCode.OK);
        var (device, _) = await StockDeviceAsync(shop);

        // An own marketplace offer (imported: net 89.90) is the fallback price: 89.90 + 19 % = 106.98 gross.
        var offerEan = Scenario.NewEan();
        var csv = $"Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand\nSKU-{marker};{offerEan};Apple;Akku{marker};Akku;ServicePack;89,90;3\n";
        var preview = (await shop.UploadImportAsync("preise.csv", csv)).Expect(HttpStatusCode.Created);
        (await shop.PostAsync($"/api/imports/{preview.Json["id"]}/commit")).Expect(HttpStatusCode.OK);

        var register = await RegisterAsync(shop);
        (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 12_99 })).Expect(HttpStatusCode.OK);

        var all = Items(await shop.GetAsync($"/api/pos/registers/{register}/products?search={marker}&pageSize=50"));
        var byName = all.ToDictionary(p => p!["name"]!.GetValue<string>().Split(' ')[0], p => p!);
        Assert.Equal(["Akku" + marker, "Display" + marker, "Kabel" + marker], byName.Keys.Order());  // sold-out product hidden, device has no marker
        Assert.Equal((12_99L, "PosPrice", 5, "Accessory"), (byName["Kabel" + marker]["priceMinor"]!.GetValue<long>(),
            byName["Kabel" + marker]["priceSource"]!.GetValue<string>(), byName["Kabel" + marker]["available"]!.GetValue<int>(),
            byName["Kabel" + marker]["category"]!.GetValue<string>()));
        Assert.Equal((106_98L, "Offer"), (byName["Akku" + marker]["priceMinor"]!.GetValue<long>(), byName["Akku" + marker]["priceSource"]!.GetValue<string>()));
        Assert.Equal("None", byName["Display" + marker]["priceSource"]!.GetValue<string>());
        Assert.Null(byName["Display" + marker]["priceMinor"]);
        Assert.Null(byName["Display" + marker]["imageUrl"]);
        Assert.False(byName["Kabel" + marker]["serialized"]!.GetValue<bool>());

        // Category filter, search by EAN, paging, out-of-stock on request, serialized devices flagged.
        Assert.Single(Items(await shop.GetAsync($"/api/pos/registers/{register}/products?search={marker}&category=display")));
        Assert.Equal("pos.category", (await shop.GetAsync($"/api/pos/registers/{register}/products?category=Kuchen")).Code);
        Assert.Single(Items(await shop.GetAsync($"/api/pos/registers/{register}/products?search={offerEan}")));
        var page2 = (await shop.GetAsync($"/api/pos/registers/{register}/products?search={marker}&pageSize=2&page=2")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((3, 1), (page2["totalCount"]!.GetValue<int>(), page2["items"]!.AsArray().Count));
        Assert.Equal(4, Items(await shop.GetAsync($"/api/pos/registers/{register}/products?search={marker}&includeOutOfStock=true")).Count);
        var all200 = Items(await shop.GetAsync($"/api/pos/registers/{register}/products?pageSize=200"));
        Assert.True(all200.Single(p => Id(p!, "productId") == device)!["serialized"]!.GetValue<bool>());
        var categories = (await shop.GetAsync($"/api/pos/registers/{register}/product-categories")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Contains(categories, c => c!["category"]!.GetValue<string>() == "Display" && c["productCount"]!.GetValue<int>() >= 1);

        // Lookup shows the till price; clearing the POS price falls back (here: none).
        var lookup = (await shop.GetAsync($"/api/pos/lookup?code={offerEan}&registerId={register}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((106_98L, "Offer"), (lookup["priceMinor"]!.GetValue<long>(), lookup["priceSource"]!.GetValue<string>()));
        var cleared = (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = (long?)null })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("None", cleared["priceSource"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 0 })).Status);
        Assert.Equal("pos.product_not_found", (await shop.PutAsync($"/api/pos/prices/{Guid.NewGuid()}", new { priceMinor = 1_00 })).Code);

        // Isolation: another shop sees neither the register nor this shop's price for the same (shared) catalog product.
        var other = await PosShopAsync();
        Assert.Equal("pos.register_not_found", (await other.GetAsync($"/api/pos/registers/{register}/products")).Code);
        (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 15_00 })).Expect(HttpStatusCode.OK);
        var foreign = (await other.GetAsync($"/api/pos/prices/{cable}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("None", foreign["priceSource"]!.GetValue<string>());
        var otherRegister = await RegisterAsync(other);
        Assert.Equal(0, (await other.GetAsync($"/api/pos/registers/{otherRegister}/products?search={marker}")).Json["totalCount"]!.GetValue<int>());

        // Price maintenance needs pos.manage.
        var (staff, _) = await StaffAsync(shop);
        Assert.Equal("permission.denied", (await staff.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 1_00 })).Code);
    }

    // ---- 3. per-cashier rights ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cashier_rights_are_enforced_per_membership_and_owners_keep_all()
    {
        var shop = await PosShopAsync();
        var cable = await StockProductAsync(shop, 50, "Kabel");
        (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 20_00 })).Expect(HttpStatusCode.OK);
        var register = await RegisterAsync(shop);
        var (staff, staffId) = await StaffAsync(shop);
        var session = await OpenSessionAsync(staff, register);

        // Defaults: staff may sell at the till price and discount, but not change the price.
        (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00), Key())).Expect(HttpStatusCode.Created);
        var overridden = await staff.PostAsync("/api/pos/sales", Sale(register, cable, 15_00), Key());
        Assert.Equal((HttpStatusCode.Forbidden, "pos.price_override_forbidden"), (overridden.Status, overridden.Code));
        Assert.Equal(20_00, overridden.Json["expectedUnitPriceMinor"]!.GetValue<long>());
        (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, discount: 10_00), Key())).Expect(HttpStatusCode.Created);
        var me = (await staff.GetAsync("/api/me")).Json["memberships"]!.AsArray().Single()!;
        Assert.Equal(100, me["maxDiscountPercent"]!.GetValue<int>());
        Assert.DoesNotContain("pos.price_override", me["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));

        // Owner grants price override and limits discounts to 10 %.
        var configured = (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights",
            new { permissions = new[] { "pos.price_override", "pos.discount" }, maxDiscountPercent = 10 })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(10, configured["maxDiscountPercent"]!.GetValue<int>());
        (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 15_00), Key())).Expect(HttpStatusCode.Created);
        (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, discount: 2_00), Key())).Expect(HttpStatusCode.Created); // exactly 10 %
        var tooMuch = await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, discount: 2_01), Key());
        Assert.Equal((HttpStatusCode.Forbidden, "pos.discount_limit_exceeded", 10), (tooMuch.Status, tooMuch.Code, tooMuch.Json["maxDiscountPercent"]!.GetValue<int>()));
        var nextBefore = (await shop.GetAsync($"/api/pos/registers/{register}")).Json["nextReceiptNumber"]!.GetValue<long>();

        // Rights withdrawn: no discount, no refund, no cash in/out, no closing, no session report.
        (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = Array.Empty<string>(), maxDiscountPercent = (int?)null }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal("pos.discount_forbidden", (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, discount: 1), Key())).Code);
        Assert.Equal("pos.price_override_forbidden", (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 19_99), Key())).Code);
        var sale = (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(nextBefore, sale["number"]!.GetValue<long>()); // refused sales gave their numbers back
        var refund = await staff.PostAsync($"/api/pos/sales/{sale["id"]}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "defekt" }, Key());
        Assert.Equal((HttpStatusCode.Forbidden, "permission.denied"), (refund.Status, refund.Code));
        Assert.Equal("permission.denied",
            (await staff.PostAsync($"/api/pos/sessions/{session}/cash-movements", new { kind = "Deposit", amountMinor = 1_00, reason = "x" })).Code);
        Assert.Equal("permission.denied", (await staff.PostAsync($"/api/pos/sessions/{session}/close", new { countedCashMinor = 0 })).Code);
        Assert.Equal("permission.denied", (await staff.GetAsync($"/api/pos/reports/sales?sessionId={session}")).Code);
        var staffList = (await shop.GetAsync("/api/organization/staff")).Expect(HttpStatusCode.OK).Json.AsArray();
        var entry = staffList.Single(s => Id(s!, "userId") == staffId)!;
        Assert.Empty(entry["posPermissions"]!.AsArray());
        Assert.Contains("pos.sell", entry["effectivePermissions"]!.AsArray().Select(p => p!.GetValue<string>()));

        // The owner can do all of it; owner/manager rights cannot be restricted; unknown permissions are refused.
        (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "defekt" }, Key())).Expect(HttpStatusCode.Created);
        (await shop.PostAsync("/api/pos/sales", Sale(register, cable, 1_00, discount: 99), Key())).Expect(HttpStatusCode.Created);
        var ownerId = Id((await shop.GetAsync("/api/me")).Json, "userId");
        Assert.Equal("staff.pos_rights_role", (await shop.PutAsync($"/api/organization/staff/{ownerId}/pos-rights", new { permissions = Array.Empty<string>() })).Code);
        var (manager, managerId) = await StaffAsync(shop, "Manager");
        Assert.Equal("staff.pos_rights_role", (await shop.PutAsync($"/api/organization/staff/{managerId}/pos-rights", new { maxDiscountPercent = 5 })).Code);
        (await manager.PostAsync("/api/pos/sales", Sale(register, cable, 5_00, discount: 4_00), Key())).Expect(HttpStatusCode.Created);
        Assert.Equal("staff.pos_permission_unknown",
            (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = new[] { "organization.manage" } })).Code);
        Assert.Equal("permission.denied", (await staff.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = new[] { "pos.discount" } })).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { maxDiscountPercent = 101 })).Status);

        // The database refuses POS rights on an owner membership, whatever the code does.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<object>(
            $"UPDATE tenancy.memberships SET pos_max_discount_percent = 5 WHERE user_id = '{ownerId}' RETURNING 1"));
        Assert.Equal("23514", ex.SqlState);
    }

    // ---- 2 + 4. customers and line notes -----------------------------------------------------------------------------------

    [Fact]
    public async Task Customers_at_the_till_are_tenant_isolated_printed_on_receipts_and_kept_out_of_the_audit_log()
    {
        var shop = await PosShopAsync();
        var cable = await StockProductAsync(shop, 10, "Kabel");
        var register = await RegisterAsync(shop);
        await OpenSessionAsync(shop, register);
        var marker = Scenario.UniqueSuffix();

        var customer = (await shop.PostAsync("/api/pos/customers", new
        {
            name = $"Erika Muster{marker}", company = "Muster GmbH", email = "erika@example.de", phone = "+49 30 123", street = "Weg 1",
            postalCode = "10115", city = "Berlin", countryCode = "de", vatId = "DE 123456789",
        })).Expect(HttpStatusCode.Created).Json;
        var customerId = Id(customer);
        Assert.Equal(("DE", "DE123456789"), (customer["countryCode"]!.GetValue<string>(), customer["vatId"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync("/api/pos/customers", new { name = "" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync("/api/pos/customers", new { name = "X", email = "kein-email" })).Status);
        Assert.Single(Items(await shop.GetAsync($"/api/pos/customers?search=muster{marker}")));
        Assert.Single(Items(await shop.GetAsync("/api/pos/customers?search=erika@example")));

        // Sale with customer and a line note; both are part of the immutable receipt.
        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, cable, 9_99, customerId: customerId, note: "Schutzfolie montiert"), Key()))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Schutzfolie montiert", sale["lines"]![0]!["note"]!.GetValue<string>());
        Assert.Equal("Schutzfolie montiert", sale["document"]!["lines"]![0]!["note"]!.GetValue<string>());
        Assert.Equal(customerId, Id(sale, "customerId"));
        Assert.Equal($"Erika Muster{marker}", sale["customer"]!["name"]!.GetValue<string>());
        Assert.Equal("Muster GmbH", sale["document"]!["customer"]!["company"]!.GetValue<string>());
        Assert.Null(sale["document"]!["customer"]!["email"]); // email/phone are never printed
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await shop.PostAsync("/api/pos/sales", Sale(register, cable, 1_00, note: new string('x', 201)), Key())).Status);

        // Changing the customer later never changes the issued receipt; a Storno carries the original customer and note.
        (await shop.PutAsync($"/api/pos/customers/{customerId}", new { name = "Erika Neu", city = "Hamburg" })).Expect(HttpStatusCode.OK);
        var reread = (await shop.GetAsync($"/api/pos/sales/{sale["id"]}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal($"Erika Muster{marker}", reread["customer"]!["name"]!.GetValue<string>());
        var storno = (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((customerId, "Schutzfolie montiert"), (Id(storno, "customerId"), storno["lines"]![0]!["note"]!.GetValue<string>()));
        Assert.Equal($"Erika Muster{marker}", storno["document"]!["customer"]!["name"]!.GetValue<string>());

        // Personal data never reaches the audit log.
        Assert.Equal(0L, await AsOwnerAsync<long>(
            $"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Customer' AND (changes::text LIKE '%Muster{marker}%' OR changes::text LIKE '%Erika Neu%' OR changes::text LIKE '%erika@example%')"));
        Assert.True(await AsOwnerAsync<long>(
            $"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Customer' AND entity_id = '{customerId}' AND changes::text LIKE '%[personal data]%'") >= 2);

        // Isolation: another tenant cannot read, change or sell to the customer.
        var other = await PosShopAsync();
        var otherCable = await StockProductAsync(other, 1, "Kabel");
        var otherRegister = await RegisterAsync(other);
        await OpenSessionAsync(other, otherRegister);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/pos/customers/{customerId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsync($"/api/pos/customers/{customerId}", new { name = "Hack" })).Status);
        Assert.Equal(0, (await other.GetAsync($"/api/pos/customers?search=Erika")).Json["totalCount"]!.GetValue<int>());
        Assert.Equal("pos.customer_not_found", (await other.PostAsync("/api/pos/sales", Sale(otherRegister, otherCable, 1_00, customerId: customerId), Key())).Code);

        // Database level: RLS hides the customer from other tenants and from requests without tenant; no DELETE grant.
        foreach (var organization in new Guid?[] { other.OrganizationId, null })
        {
            await using var app = new NpgsqlConnection(fixture.AppConnectionString);
            await app.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", app))
            {
                set.Parameters.AddWithValue("org", organization?.ToString() ?? string.Empty);
                await set.ExecuteNonQueryAsync();
            }
            foreach (var table in new[] { "customers", "prices", "receipt_reprints" })
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM pos.{table} WHERE organization_id = '{shop.OrganizationId}'", app);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            }
            await using var view = new NpgsqlCommand($"SELECT count(*) FROM pos.sellable_stock WHERE organization_id = '{shop.OrganizationId}'", app);
            Assert.Equal(0L, (long)(await view.ExecuteScalarAsync())!);
        }
        await using (var app = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await app.OpenAsync();
            await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_org', '{shop.OrganizationId}', false)", app))
                await set.ExecuteNonQueryAsync();
            await using var delete = new NpgsqlCommand($"DELETE FROM pos.customers WHERE id = '{customerId}'", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        }
    }

    // ---- 5. order history and reprints -------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_history_filters_and_reprints_are_recorded_without_changing_the_receipt()
    {
        var shop = await PosShopAsync();
        var cable = await StockProductAsync(shop, 20, "Kabel");
        var register = await RegisterAsync(shop);
        await OpenSessionAsync(shop, register);
        var (staff, staffId) = await StaffAsync(shop);
        var customer = Id((await shop.PostAsync("/api/pos/customers", new { name = "Stammkunde" })).Expect(HttpStatusCode.Created).Json);

        var cash = (await shop.PostAsync("/api/pos/sales", Sale(register, cable, 5_00), Key())).Expect(HttpStatusCode.Created).Json;
        var card = (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 50_00, method: "Card", customerId: customer), Key())).Expect(HttpStatusCode.Created).Json;
        var big = (await shop.PostAsync("/api/pos/sales", Sale(register, cable, 200_00), Key())).Expect(HttpStatusCode.Created).Json;
        var returned = (await shop.PostAsync($"/api/pos/sales/{big["id"]}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "zurück" }, Key())).Expect(HttpStatusCode.Created).Json;

        IEnumerable<long> Numbers(ApiResponse r) => Items(r).Select(i => i!["number"]!.GetValue<long>()).Order();
        var basePath = $"/api/pos/sales?registerId={register}";
        Assert.Equal([1L, 2, 3, 4], Numbers(await shop.GetAsync(basePath)));
        Assert.Equal([2L], Numbers(await shop.GetAsync($"{basePath}&number=2")));
        Assert.Equal([2L], Numbers(await shop.GetAsync($"{basePath}&paymentMethod=card")));
        Assert.Equal([1L, 3, 4], Numbers(await shop.GetAsync($"{basePath}&paymentMethod=Cash")));
        Assert.Equal([2L, 3], Numbers(await shop.GetAsync($"{basePath}&minTotalMinor=1000")));
        Assert.Equal([1L, 2], Numbers(await shop.GetAsync($"{basePath}&minTotalMinor=0&maxTotalMinor=5000")));
        Assert.Equal([4L], Numbers(await shop.GetAsync($"{basePath}&maxTotalMinor=-1")));
        Assert.Equal([2L], Numbers(await shop.GetAsync($"{basePath}&cashierId={staffId}")));
        Assert.Equal([2L], Numbers(await shop.GetAsync($"{basePath}&customerId={customer}")));
        Assert.Equal([4L], Numbers(await shop.GetAsync($"{basePath}&kind=Return")));
        var issued = DateTimeOffset.Parse(card["createdAt"]!.GetValue<string>());
        Assert.Equal([2L], Numbers(await shop.GetAsync(
            // ±1 µs around the stored time (PostgreSQL keeps microseconds; the DTO may carry 100 ns ticks).
            $"{basePath}&from={Uri.EscapeDataString(issued.AddTicks(-10).ToString("O"))}&to={Uri.EscapeDataString(issued.AddTicks(10).ToString("O"))}")));
        Assert.Empty(Numbers(await shop.GetAsync($"{basePath}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}")));
        Assert.Equal("pos.payment_method", (await shop.GetAsync($"{basePath}&paymentMethod=Gold")).Code);
        Assert.Equal("pos.amount_range", (await shop.GetAsync($"{basePath}&minTotalMinor=10&maxTotalMinor=5")).Code);
        var summary = Items(await shop.GetAsync($"{basePath}&number=2")).Single()!;
        Assert.Equal(("Stammkunde", "Card", 0), (summary["customerName"]!.GetValue<string>(), summary["paymentMethods"]![0]!.GetValue<string>(),
            summary["reprintCount"]!.GetValue<int>()));
        Assert.False(string.IsNullOrEmpty(summary["createdByName"]?.GetValue<string>()));
        Assert.Equal(Id(returned), Id(Items(await shop.GetAsync($"{basePath}&kind=Return")).Single()!));

        // Reprints: numbered copies, recorded append-only; the receipt document is byte-for-byte the original.
        var documentBefore = (await shop.GetAsync($"/api/pos/sales/{cash["id"]}")).Json["document"]!.ToJsonString();
        var first = (await staff.PostAsync($"/api/pos/sales/{cash["id"]}/reprints")).Expect(HttpStatusCode.Created).Json;
        var second = (await shop.PostAsync($"/api/pos/sales/{cash["id"]}/reprints")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((1, 2), (first["sequence"]!.GetValue<int>(), second["sequence"]!.GetValue<int>()));
        Assert.Equal(2, second["receipt"]!["reprintCount"]!.GetValue<int>());
        var after = (await shop.GetAsync($"/api/pos/sales/{cash["id"]}")).Json;
        Assert.Equal(documentBefore, after["document"]!.ToJsonString());
        Assert.Equal(2, after["reprintCount"]!.GetValue<int>());
        Assert.NotNull(after["lastReprintedAt"]);
        Assert.Equal(2, Items(await shop.GetAsync($"{basePath}&number=1")).Single()!["reprintCount"]!.GetValue<int>());
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => shop.PostAsync($"/api/pos/sales/{card["id"]}/reprints")));
        Assert.Equal([1, 2, 3, 4, 5], concurrent.Select(r => r.Expect(HttpStatusCode.Created).Json["sequence"]!.GetValue<int>()).Order());
        var otherShop = await PosShopAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await otherShop.PostAsync($"/api/pos/sales/{cash["id"]}/reprints")).Status);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<object>($"UPDATE pos.receipt_reprints SET sequence = 9 WHERE receipt_id = '{cash["id"]}' RETURNING 1"));
        Assert.Equal("42501", ex.SqlState);
    }

    // ---- 6. cash counts, reasons, reports ----------------------------------------------------------------------------------

    [Fact]
    public async Task Denomination_counts_reasons_and_reports_match_expected_cash()
    {
        var shop = await PosShopAsync();
        var cable = await StockProductAsync(shop, 20, "Kabel");
        var case_ = await StockProductAsync(shop, 20, "Hülle");
        var register = await RegisterAsync(shop);
        var (staff, staffId) = await StaffAsync(shop);

        // Opening by denominations: 2 × 50 € + 5 × 1 € + 10 × 0.50 € = 110.00 €.
        object[] opening = [new { valueMinor = 5_000, count = 2 }, new { valueMinor = 100, count = 5 }, new { valueMinor = 50, count = 10 }];
        var mismatch = await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 100_00, denominations = opening });
        Assert.Equal("pos.denominations_mismatch", mismatch.Code);
        Assert.Equal("pos.denomination_unknown",
            (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { denominations = new[] { new { valueMinor = 300, count = 1 } } })).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { })).Status);
        var session = (await staff.PostAsync($"/api/pos/registers/{register}/sessions", new { denominations = opening })).Expect(HttpStatusCode.Created).Json;
        var sessionId = Id(session);
        Assert.Equal(110_00, session["openingCashMinor"]!.GetValue<long>());
        Assert.Equal(5_000, session["openingDenominations"]![0]!["valueMinor"]!.GetValue<long>()); // largest first

        // Sales: cash 2 × 10 € (shop), card 25 € (staff), cash 7.50 € with 10 € tendered (staff); a cash return of 10 €.
        var s1 = (await shop.PostAsync("/api/pos/sales", Sale(register, cable, 10_00, quantity: 2), Key())).Expect(HttpStatusCode.Created).Json;
        (await staff.PostAsync("/api/pos/sales", Sale(register, case_, 25_00, method: "Card"), Key())).Expect(HttpStatusCode.Created);
        (await staff.PostAsync("/api/pos/sales", Sale(register, case_, 7_50, paid: 10_00), Key())).Expect(HttpStatusCode.Created);
        (await shop.PostAsync($"/api/pos/sales/{s1["id"]}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "Umtausch" }, Key())).Expect(HttpStatusCode.Created);

        // Cash in/out with predefined reasons.
        var bank = (await staff.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Withdrawal", amountMinor = 50_00, reasonCode = "BankDeposit" }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("Bankeinzahlung", "BankDeposit"), (bank["reason"]!.GetValue<string>(), bank["reasonCode"]!.GetValue<string>()));
        var fund = (await staff.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements",
            new { kind = "Deposit", amountMinor = 20_00, reasonCode = "ChangeFund", reason = "Rollen 2 €" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Rollen 2 €", fund["reason"]!.GetValue<string>());
        Assert.Equal("pos.reason", (await staff.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements",
            new { kind = "Withdrawal", amountMinor = 1_00, reasonCode = "Other" })).Code);
        Assert.Equal("pos.reason_code", (await staff.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements",
            new { kind = "Withdrawal", amountMinor = 1_00, reasonCode = "ChangeFund" })).Code);
        var reasons = (await staff.GetAsync("/api/pos/cash-movement-reasons")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Contains(reasons, r => r!["code"]!.GetValue<string>() == "OwnerWithdrawal" && r["kind"]!.GetValue<string>() == "Withdrawal");

        // Expected = 110.00 + 20.00 + 7.50 − 10.00 (return) − 50.00 + 20.00 = 97.50 €.
        var live = (await staff.GetAsync($"/api/pos/sessions/{sessionId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(97_50, live["expectedCashMinor"]!.GetValue<long>());

        // The opening count can never be changed afterwards, not even by the owner role.
        var fixedEx = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<object>($"UPDATE pos.cash_sessions SET opening_cash_minor = 1 WHERE id = '{sessionId}' RETURNING 1"));
        Assert.Equal("42501", fixedEx.SqlState);

        // Session report (staff with close right) — by product, cashier and payment method.
        var report = (await staff.GetAsync($"/api/pos/reports/sales?sessionId={sessionId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((4, 42_50L), (report["receiptCount"]!.GetValue<int>(), report["totalGrossMinor"]!.GetValue<long>()));
        var byProduct = report["byProduct"]!.AsArray().ToDictionary(p => Id(p!, "productId"), p => p!);
        Assert.Equal((2, 1, 1, 10_00L), (byProduct[cable]["soldQuantity"]!.GetValue<int>(), byProduct[cable]["returnedQuantity"]!.GetValue<int>(),
            byProduct[cable]["netQuantity"]!.GetValue<int>(), byProduct[cable]["grossMinor"]!.GetValue<long>()));
        Assert.Equal((2, 32_50L), (byProduct[case_]["netQuantity"]!.GetValue<int>(), byProduct[case_]["grossMinor"]!.GetValue<long>()));
        var byCashier = report["byCashier"]!.AsArray().ToDictionary(c => Id(c!, "userId"), c => c!);
        Assert.Equal((2, 0, 32_50L), (byCashier[staffId]["saleCount"]!.GetValue<int>(), byCashier[staffId]["refundCount"]!.GetValue<int>(),
            byCashier[staffId]["grossMinor"]!.GetValue<long>()));
        var byMethod = report["byPaymentMethod"]!.AsArray().ToDictionary(m => m!["method"]!.GetValue<string>(), m => m!["amountMinor"]!.GetValue<long>());
        Assert.Equal((17_50L, 25_00L), (byMethod["Cash"], byMethod["Card"])); // 20 + 7.50 − 10

        // Date-range report needs pos.manage.
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        Assert.Equal("permission.denied", (await staff.GetAsync($"/api/pos/reports/sales?from={from}&to={to}")).Code);
        var range = (await shop.GetAsync($"/api/pos/reports/sales?from={from}&to={to}&registerId={register}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(42_50L, range["totalGrossMinor"]!.GetValue<long>());
        Assert.Equal("pos.date_range", (await shop.GetAsync("/api/pos/reports/sales")).Code);

        // Closing by denominations: 1 × 50 € + 2 × 20 € + 1 × 5 € + 2 × 1 € = 97.00 € → 0.50 € missing.
        object[] closing = [new { valueMinor = 5_000, count = 1 }, new { valueMinor = 2_000, count = 2 }, new { valueMinor = 500, count = 1 },
            new { valueMinor = 100, count = 2 }, new { valueMinor = 1, count = 0 }];
        Assert.Equal("pos.denominations_mismatch",
            (await staff.PostAsync($"/api/pos/sessions/{sessionId}/close", new { countedCashMinor = 97_50, denominations = closing })).Code);
        var closed = (await staff.PostAsync($"/api/pos/sessions/{sessionId}/close", new { denominations = closing, note = "Kasse gezählt" }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal((97_50L, 97_00L, -50L), (closed["expectedCashMinor"]!.GetValue<long>(), closed["countedCashMinor"]!.GetValue<long>(),
            closed["differenceMinor"]!.GetValue<long>()));
        Assert.Equal(4, closed["closingDenominations"]!.AsArray().Count); // zero counts dropped
        Assert.Equal(97_50L, closed["summary"]!["expectedCashMinor"]!.GetValue<long>());
        Assert.Equal(97_00L, await AsOwnerAsync<long>(
            $"SELECT sum((d->>'valueMinor')::bigint * (d->>'count')::bigint)::bigint FROM pos.cash_sessions, jsonb_array_elements(closing_denominations) d WHERE id = '{sessionId}'"));
    }
}
