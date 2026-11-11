using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Price lists (ADR-0020) on real PostgreSQL: price lists and item prices, the customer's price list, resolution in the
/// product grid, price read and lookup, selling at the list price without the price-override right, and the snapshot on the
/// receipt.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosPricelistTests(HandyNetFixture fixture)
{
    private readonly PosSupport _pos = new(fixture);

    private static Guid Id(JsonNode node, string property = "id") => PosSupport.Id(node, property);

    private static object Customer(string name, Guid? pricelistId = null) => new { name, pricelistId };

    private static object Sale(Guid register, Guid product, long price, Guid? customerId, int quantity = 1) => new
    {
        registerId = register,
        lines = new[] { new { productId = product, quantity, unitPriceMinor = price } },
        payments = new[] { new { method = "Cash", amountMinor = price * quantity } },
        customerId,
    };

    [Fact]
    public async Task Price_lists_and_items_are_managed_by_managers_and_read_by_the_till()
    {
        var shop = await _pos.ShopAsync();
        var cable = await PosSupport.StockProductAsync(shop, 5, "Ladekabel");
        var caseProduct = await PosSupport.StockProductAsync(shop, 5, "Hülle");
        (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 20_00 })).Expect(HttpStatusCode.OK);
        Assert.Empty((await shop.GetAsync("/api/pos/pricelists")).Expect(HttpStatusCode.OK).Json.AsArray());

        var created = (await shop.PostAsync("/api/pos/pricelists", new { name = "Händler", discountBasisPoints = 1000 })).Expect(HttpStatusCode.Created).Json;
        var id = Id(created);
        Assert.Equal((1000, true, true, 0, 0), (created["discountBasisPoints"]!.GetValue<int>(), created["isActive"]!.GetValue<bool>(),
            created["currentlyValid"]!.GetValue<bool>(), created["itemCount"]!.GetValue<int>(), created["customerCount"]!.GetValue<int>()));
        var duplicate = await shop.PostAsync("/api/pos/pricelists", new { name = "Händler" });
        Assert.Equal((HttpStatusCode.Conflict, "pos.pricelist_name_taken"), (duplicate.Status, duplicate.Code));
        var tooMuch = await shop.PostAsync("/api/pos/pricelists", new { name = "Zu viel", discountBasisPoints = 10_001 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.Status);
        var wrongDates = await shop.PostAsync("/api/pos/pricelists", new { name = "Daten", validFrom = "2026-12-31", validTo = "2026-01-01" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.pricelist_validity"), (wrongDates.Status, wrongDates.Code));

        // Items: set, change, list with product data and base price, search, delete.
        var item = (await shop.PutAsync($"/api/pos/pricelists/{id}/items/{cable}", new { priceMinor = 15_00 })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((15_00L, 20_00L), (item["priceMinor"]!.GetValue<long>(), item["basePriceMinor"]!.GetValue<long>()));
        (await shop.PutAsync($"/api/pos/pricelists/{id}/items/{cable}", new { priceMinor = 14_00 })).Expect(HttpStatusCode.OK);
        (await shop.PutAsync($"/api/pos/pricelists/{id}/items/{caseProduct}", new { priceMinor = 7_50 })).Expect(HttpStatusCode.OK);
        (await shop.PutAsync($"/api/pos/pricelists/{id}/items/{cable}", new { priceMinor = 0 })).Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("pos.product_not_found", (await shop.PutAsync($"/api/pos/pricelists/{id}/items/{Guid.NewGuid()}", new { priceMinor = 1_00 })).Code);
        var items = (await shop.GetAsync($"/api/pos/pricelists/{id}/items")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, items["totalCount"]!.GetValue<int>());
        var found = (await shop.GetAsync($"/api/pos/pricelists/{id}/items?search=ladekabel")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal((cable, 14_00L), (Id(found.Single()!, "productId"), found.Single()!["priceMinor"]!.GetValue<long>()));
        Assert.Null((await shop.GetAsync($"/api/pos/pricelists/{id}/items?search=hülle")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single()!["basePriceMinor"]);
        (await PosSupport.DeleteAsync(shop, $"/api/pos/pricelists/{id}/items/{caseProduct}")).Expect(HttpStatusCode.NoContent);
        Assert.Equal("pos.pricelist_item_not_found", (await PosSupport.DeleteAsync(shop, $"/api/pos/pricelists/{id}/items/{caseProduct}")).Code);
        Assert.Equal(1, (await shop.GetAsync($"/api/pos/pricelists/{id}")).Expect(HttpStatusCode.OK).Json["itemCount"]!.GetValue<int>());

        // Update with the version; stale version refused.
        var version = (await shop.GetAsync($"/api/pos/pricelists/{id}")).Expect(HttpStatusCode.OK).Json["version"]!.GetValue<uint>();
        Assert.Equal("pos.pricelist_version", (await shop.PutAsync($"/api/pos/pricelists/{id}", new { name = "Händler", discountBasisPoints = 500 })).Code);
        var updated = (await shop.PutAsync($"/api/pos/pricelists/{id}", new { name = "Händler A", discountBasisPoints = 500, isActive = true, validTo = "2099-12-31", version }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Händler A", 500, "2099-12-31"), (updated["name"]!.GetValue<string>(), updated["discountBasisPoints"]!.GetValue<int>(), updated["validTo"]!.GetValue<string>()));
        var stale = await shop.PutAsync($"/api/pos/pricelists/{id}", new { name = "X", version });
        Assert.Equal((HttpStatusCode.Conflict, "concurrency"), (stale.Status, stale.Code));

        // Staff (pos.sell) reads, but cannot write lists or items; changes are audited.
        var staff = await _pos.MemberAsync(shop, "Staff");
        Assert.Single((await staff.GetAsync("/api/pos/pricelists")).Expect(HttpStatusCode.OK).Json.AsArray());
        (await staff.GetAsync($"/api/pos/pricelists/{id}/items")).Expect(HttpStatusCode.OK);
        (await staff.PostAsync("/api/pos/pricelists", new { name = "Eigene" })).Expect(HttpStatusCode.Forbidden);
        (await staff.PutAsync($"/api/pos/pricelists/{id}", new { name = "X", version = 1 })).Expect(HttpStatusCode.Forbidden);
        (await staff.PutAsync($"/api/pos/pricelists/{id}/items/{cable}", new { priceMinor = 1 })).Expect(HttpStatusCode.Forbidden);
        (await PosSupport.DeleteAsync(staff, $"/api/pos/pricelists/{id}/items/{cable}")).Expect(HttpStatusCode.Forbidden);
        Assert.True(await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Pricelist' AND entity_id = '{id}'") >= 2);
        Assert.True(await _pos.OwnerScalarAsync<long>("SELECT count(*) FROM infra.audit_log WHERE entity_type = 'PricelistItem' AND action ILIKE '%delete%'") >= 1);
    }

    [Fact]
    public async Task Customer_price_list_changes_grid_price_and_lookup_and_sells_without_the_override_right()
    {
        var shop = await _pos.ShopAsync();
        var cable = await PosSupport.StockProductAsync(shop, 20, "Ladekabel");
        var caseProduct = await PosSupport.StockProductAsync(shop, 20, "Hülle");
        var noPrice = await PosSupport.StockProductAsync(shop, 20, "Folie");
        (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 20_00 })).Expect(HttpStatusCode.OK);
        (await shop.PutAsync($"/api/pos/prices/{caseProduct}", new { priceMinor = 9_99 })).Expect(HttpStatusCode.OK);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var list = Id((await shop.PostAsync("/api/pos/pricelists", new { name = "Händler", discountBasisPoints = 1000 })).Expect(HttpStatusCode.Created).Json);
        (await shop.PutAsync($"/api/pos/pricelists/{list}/items/{cable}", new { priceMinor = 15_00 })).Expect(HttpStatusCode.OK);

        // Customers: with and without the list; an unknown list is refused; the DTO names the list.
        var dealer = (await shop.PostAsync("/api/pos/customers", Customer("Händler Müller", list))).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((list, "Händler"), (Id(dealer, "pricelistId"), dealer["pricelistName"]!.GetValue<string>()));
        var walkIn = Id((await shop.PostAsync("/api/pos/customers", Customer("Laufkunde"))).Expect(HttpStatusCode.Created).Json);
        Assert.Equal("pos.pricelist_not_found", (await shop.PostAsync("/api/pos/customers", Customer("X", Guid.NewGuid()))).Code);
        Assert.Equal(1, (await shop.GetAsync($"/api/pos/pricelists/{list}")).Expect(HttpStatusCode.OK).Json["customerCount"]!.GetValue<int>());
        Assert.Equal("Händler", (await shop.GetAsync("/api/pos/customers?search=Müller")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single()!["pricelistName"]!.GetValue<string>());

        async Task<(long? Price, string Source, long? Base)> GridAsync(Guid product, Guid? customer)
        {
            var row = (await shop.GetAsync($"/api/pos/registers/{register}/products?pageSize=100" + (customer is null ? "" : $"&customerId={customer}")))
                .Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single(i => Id(i!, "productId") == product)!;
            return (row["priceMinor"]?.GetValue<long>(), row["priceSource"]!.GetValue<string>(), row["basePriceMinor"]?.GetValue<long>());
        }

        // Item price → general discount on the base → nothing without a base; other customers and no customer see the base.
        Assert.Equal((15_00L, "Pricelist", 20_00L), await GridAsync(cable, Id(dealer)));
        Assert.Equal((8_99L, "Pricelist", 9_99L), await GridAsync(caseProduct, Id(dealer))); // 9.99 − round(0.999) = 8.99
        Assert.Equal((null, "None", null), await GridAsync(noPrice, Id(dealer)));
        Assert.Equal((20_00L, "PosPrice", 20_00L), await GridAsync(cable, walkIn));
        Assert.Equal((20_00L, "PosPrice", 20_00L), await GridAsync(cable, null));

        var price = (await shop.GetAsync($"/api/pos/prices/{cable}?registerId={register}&customerId={Id(dealer)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((15_00L, "Pricelist", 20_00L, 20_00L, "Händler"), (price["priceMinor"]!.GetValue<long>(), price["priceSource"]!.GetValue<string>(),
            price["basePriceMinor"]!.GetValue<long>(), price["posPriceMinor"]!.GetValue<long>(), price["pricelistName"]!.GetValue<string>()));
        var plain = (await shop.GetAsync($"/api/pos/prices/{cable}?registerId={register}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((20_00L, "PosPrice", 20_00L), (plain["priceMinor"]!.GetValue<long>(), plain["priceSource"]!.GetValue<string>(), plain["basePriceMinor"]!.GetValue<long>()));
        Assert.Null(plain["pricelistId"]);

        var ean = (await shop.GetAsync($"/api/pos/registers/{register}/products?pageSize=100")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Single(i => Id(i!, "productId") == caseProduct)!["ean"]!.GetValue<string>();
        var lookup = (await shop.GetAsync($"/api/pos/lookup?code={ean}&registerId={register}&customerId={Id(dealer)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((8_99L, "Pricelist", 9_99L), (lookup["priceMinor"]!.GetValue<long>(), lookup["priceSource"]!.GetValue<string>(), lookup["basePriceMinor"]!.GetValue<long>()));
        Assert.Equal("pos.customer_not_found", (await shop.GetAsync($"/api/pos/lookup?code={ean}&registerId={register}&customerId={Guid.NewGuid()}")).Code);
        Assert.Equal("pos.customer_not_found", (await shop.GetAsync($"/api/pos/registers/{register}/products?customerId={Guid.NewGuid()}")).Code);

        // A cashier without pos.price_override sells at the list price — but only to that customer.
        var staff = await _pos.MemberAsync(shop, "Staff");
        var toWalkIn = await staff.PostAsync("/api/pos/sales", Sale(register, cable, 15_00, walkIn), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Forbidden, "pos.price_override_forbidden"), (toWalkIn.Status, toWalkIn.Code));
        var anonymous = await staff.PostAsync("/api/pos/sales", Sale(register, cable, 15_00, null), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Forbidden, "pos.price_override_forbidden"), (anonymous.Status, anonymous.Code));
        var atBase = await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, Id(dealer)), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Forbidden, "pos.price_override_forbidden", 15_00L), (atBase.Status, atBase.Code, atBase.Json["expectedUnitPriceMinor"]!.GetValue<long>()));

        var receipt = (await staff.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new[] { new { productId = cable, quantity = 2, unitPriceMinor = 15_00 }, new { productId = caseProduct, quantity = 1, unitPriceMinor = 8_99 } },
            payments = new[] { new { method = "Cash", amountMinor = 38_99 } },
            customerId = Id(dealer),
        }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((38_99L, list, "Händler"), (receipt["totalGrossMinor"]!.GetValue<long>(), Id(receipt, "pricelistId"), receipt["pricelistName"]!.GetValue<string>()));
        Assert.Contains("Preisliste: Händler", receipt["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(list, await _pos.OwnerScalarAsync<Guid>($"SELECT pricelist_id FROM pos.receipts WHERE id = '{Id(receipt)}'"));

        // The walk-in customer's receipt carries no price list.
        var regular = (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, walkIn), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Null(regular["pricelistId"]);
        Assert.DoesNotContain(regular["document"]!["notes"]!.AsArray(), n => n!.GetValue<string>().StartsWith("Preisliste"));

        // Renaming the list later does not change the issued receipt (snapshot); the price list stays referenced.
        var version = (await shop.GetAsync($"/api/pos/pricelists/{list}")).Expect(HttpStatusCode.OK).Json["version"]!.GetValue<uint>();
        (await shop.PutAsync($"/api/pos/pricelists/{list}", new { name = "Großhandel", discountBasisPoints = 1000, version })).Expect(HttpStatusCode.OK);
        Assert.Equal("Händler", (await shop.GetAsync($"/api/pos/sales/{Id(receipt)}")).Expect(HttpStatusCode.OK).Json["pricelistName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Inactive_or_expired_price_lists_do_not_apply_and_only_managers_assign_them()
    {
        var shop = await _pos.ShopAsync();
        var cable = await PosSupport.StockProductAsync(shop, 20, "Ladekabel");
        (await shop.PutAsync($"/api/pos/prices/{cable}", new { priceMinor = 20_00 })).Expect(HttpStatusCode.OK);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var list = (await shop.PostAsync("/api/pos/pricelists", new { name = "Aktion", discountBasisPoints = 2500 })).Expect(HttpStatusCode.Created).Json;
        var customer = Id((await shop.PostAsync("/api/pos/customers", Customer("Stammkunde", Id(list)))).Expect(HttpStatusCode.Created).Json);

        async Task<(long Price, string Source)> PriceAsync()
        {
            var price = (await shop.GetAsync($"/api/pos/prices/{cable}?registerId={register}&customerId={customer}")).Expect(HttpStatusCode.OK).Json;
            return (price["priceMinor"]!.GetValue<long>(), price["priceSource"]!.GetValue<string>());
        }
        async Task<uint> VersionAsync() => (await shop.GetAsync($"/api/pos/pricelists/{Id(list)}")).Expect(HttpStatusCode.OK).Json["version"]!.GetValue<uint>();

        Assert.Equal((15_00L, "Pricelist"), await PriceAsync());

        // Expired yesterday (dates are inclusive days): the base price again, and the list price needs the override right.
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd");
        var expired = (await shop.PutAsync($"/api/pos/pricelists/{Id(list)}", new { name = "Aktion", discountBasisPoints = 2500, isActive = true, validTo = yesterday, version = await VersionAsync() }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.False(expired["currentlyValid"]!.GetValue<bool>());
        Assert.Equal((20_00L, "PosPrice"), await PriceAsync());
        var staff = await _pos.MemberAsync(shop, "Staff");
        Assert.Equal("pos.price_override_forbidden", (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 15_00, customer), PosSupport.Key())).Code);
        var atBase = (await staff.PostAsync("/api/pos/sales", Sale(register, cable, 20_00, customer), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Null(atBase["pricelistId"]);

        // Not yet valid, then valid again, then deactivated.
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd");
        (await shop.PutAsync($"/api/pos/pricelists/{Id(list)}", new { name = "Aktion", discountBasisPoints = 2500, isActive = true, validFrom = tomorrow, version = await VersionAsync() }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal((20_00L, "PosPrice"), await PriceAsync());
        (await shop.PutAsync($"/api/pos/pricelists/{Id(list)}", new { name = "Aktion", discountBasisPoints = 2500, isActive = true, version = await VersionAsync() })).Expect(HttpStatusCode.OK);
        Assert.Equal((15_00L, "Pricelist"), await PriceAsync());
        (await shop.PutAsync($"/api/pos/pricelists/{Id(list)}", new { name = "Aktion", discountBasisPoints = 2500, isActive = false, version = await VersionAsync() })).Expect(HttpStatusCode.OK);
        Assert.Equal((20_00L, "PosPrice"), await PriceAsync());

        // Staff may edit a customer but not give or take a price list (that would be a self-service discount).
        var other = Id((await staff.PostAsync("/api/pos/customers", Customer("Neu"))).Expect(HttpStatusCode.Created).Json);
        var assign = await staff.PutAsync($"/api/pos/customers/{other}", Customer("Neu", Id(list)));
        Assert.Equal((HttpStatusCode.Forbidden, "permission.denied"), (assign.Status, assign.Code));
        var create = await staff.PostAsync("/api/pos/customers", Customer("Mit Liste", Id(list)));
        Assert.Equal((HttpStatusCode.Forbidden, "permission.denied"), (create.Status, create.Code));
        var remove = await staff.PutAsync($"/api/pos/customers/{customer}", Customer("Stammkunde"));
        Assert.Equal((HttpStatusCode.Forbidden, "permission.denied"), (remove.Status, remove.Code));
        // Keeping the list while changing something else is fine.
        var kept = (await staff.PutAsync($"/api/pos/customers/{customer}", Customer("Stammkunde GmbH", Id(list)))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((Id(list), "Stammkunde GmbH"), (Id(kept, "pricelistId"), kept["name"]!.GetValue<string>()));
        // The manager removes it.
        var removed = (await shop.PutAsync($"/api/pos/customers/{customer}", Customer("Stammkunde GmbH"))).Expect(HttpStatusCode.OK).Json;
        Assert.Null(removed["pricelistId"]);
    }
}
