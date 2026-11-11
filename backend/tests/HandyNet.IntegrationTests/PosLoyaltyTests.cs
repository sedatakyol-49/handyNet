using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Loyalty points (ADR-0020) on real PostgreSQL: settings, earning in the sale transaction, redemption as a price reduction
/// across the eligible lines, storno and returns, manual adjustments, and the guarantee that points never go below zero.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosLoyaltyTests(HandyNetFixture fixture)
{
    private readonly PosSupport _pos = new(fixture);

    private static Guid Id(JsonNode node, string property = "id") => PosSupport.Id(node, property);

    private static async Task EnableAsync(ApiClient shop, int pointsPerEuro = 1, int pointValueMinor = 1, int minRedeemPoints = 0, bool enabled = true) =>
        (await shop.PutAsync("/api/pos/loyalty/settings", new { enabled, pointsPerEuro, pointValueMinor, minRedeemPoints })).Expect(HttpStatusCode.OK);

    private static async Task<Guid> CustomerAsync(ApiClient shop, string name = "Erika Beispiel") =>
        Id((await shop.PostAsync("/api/pos/customers", new { name })).Expect(HttpStatusCode.Created).Json);

    private static async Task<int> PointsAsync(ApiClient shop, Guid customer) =>
        (await shop.GetAsync($"/api/pos/customers/{customer}/loyalty")).Expect(HttpStatusCode.OK).Json["points"]!.GetValue<int>();

    private static object Sale(Guid register, Guid product, long price, int quantity, long pay, Guid? customerId, int? redeemPoints = null, long discount = 0) => new
    {
        registerId = register,
        lines = new[] { new { productId = product, quantity, unitPriceMinor = price, discountMinor = discount } },
        payments = new[] { new { method = "Cash", amountMinor = pay } },
        customerId,
        redeemPoints,
    };

    [Fact]
    public async Task Settings_default_to_disabled_and_are_changed_by_managers_only()
    {
        var shop = await _pos.ShopAsync();
        var defaults = (await shop.GetAsync("/api/pos/loyalty/settings")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((false, 0, 1, 0), (defaults["enabled"]!.GetValue<bool>(), defaults["pointsPerEuro"]!.GetValue<int>(), defaults["pointValueMinor"]!.GetValue<int>(),
            defaults["minRedeemPoints"]!.GetValue<int>()));
        Assert.Null(defaults["updatedAt"]);

        var saved = (await shop.PutAsync("/api/pos/loyalty/settings", new { enabled = true, pointsPerEuro = 2, pointValueMinor = 5, minRedeemPoints = 100 })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((true, 2, 5, 100), (saved["enabled"]!.GetValue<bool>(), saved["pointsPerEuro"]!.GetValue<int>(), saved["pointValueMinor"]!.GetValue<int>(),
            saved["minRedeemPoints"]!.GetValue<int>()));
        Assert.NotNull(saved["updatedAt"]);
        foreach (var invalid in new object[]
                 {
                     new { enabled = true, pointsPerEuro = 101, pointValueMinor = 1 }, new { enabled = true, pointsPerEuro = -1, pointValueMinor = 1 },
                     new { enabled = true, pointsPerEuro = 1, pointValueMinor = 0 }, new { enabled = true, pointsPerEuro = 1, pointValueMinor = 101 },
                     new { enabled = true, pointsPerEuro = 1, pointValueMinor = 1, minRedeemPoints = -5 },
                 })
            (await shop.PutAsync("/api/pos/loyalty/settings", invalid)).Expect(HttpStatusCode.UnprocessableEntity);

        var staff = await _pos.MemberAsync(shop, "Staff");
        Assert.Equal(2, (await staff.GetAsync("/api/pos/loyalty/settings")).Expect(HttpStatusCode.OK).Json["pointsPerEuro"]!.GetValue<int>());
        (await staff.PutAsync("/api/pos/loyalty/settings", new { enabled = false, pointsPerEuro = 100, pointValueMinor = 100 })).Expect(HttpStatusCode.Forbidden);
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.loyalty_settings WHERE organization_id = '{shop.OrganizationId}'"));
        Assert.True(await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'LoyaltySettings' AND organization_id = '{shop.OrganizationId}'") >= 1);
    }

    [Fact]
    public async Task Sales_earn_points_for_customers_and_redemption_reduces_the_price_not_the_payment()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 50);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var customer = await CustomerAsync(shop);

        // Disabled: nothing is earned, redeeming is refused.
        var before = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1, 10_00, customer), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((0, null as int?), (before["loyaltyPointsEarned"]!.GetValue<int>(), before["loyaltyBalanceAfter"]?.GetValue<int>()));
        var disabled = await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1, 10_00, customer, redeemPoints: 10), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.loyalty_disabled"), (disabled.Status, disabled.Code));

        await EnableAsync(shop, pointsPerEuro: 2, pointValueMinor: 5, minRedeemPoints: 20);

        // 3 × 19.99 − 0.50 discount = 59.47 → 59 full euros × 2 = 118 points. Without a customer nothing is earned.
        var first = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 19_99, 3, 60_00, customer, discount: 50), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((118, 118, 0, 0L), (first["loyaltyPointsEarned"]!.GetValue<int>(), first["loyaltyBalanceAfter"]!.GetValue<int>(),
            first["loyaltyPointsRedeemed"]!.GetValue<int>(), first["loyaltyDiscountMinor"]!.GetValue<long>()));
        Assert.Contains("Treuepunkte gesammelt: 118", first["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("Punktestand: 118", first["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        var anonymous = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1, 10_00, null), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(0, anonymous["loyaltyPointsEarned"]!.GetValue<int>());
        Assert.Null(anonymous["document"]!["loyalty"]);
        Assert.Equal(118, await PointsAsync(shop, customer));
        Assert.Equal(118, (await shop.GetAsync($"/api/pos/customers/{customer}")).Expect(HttpStatusCode.OK).Json["loyaltyPoints"]!.GetValue<int>());
        Assert.Equal(118, (await shop.GetAsync("/api/pos/customers?search=Erika")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single()!["loyaltyPoints"]!.GetValue<int>());

        // Refused redemptions: no customer, below the minimum, more than the balance, more than the eligible total.
        async Task RefusedAsync(object body, HttpStatusCode status, string code)
        {
            var response = await shop.PostAsync("/api/pos/sales", body, PosSupport.Key());
            Assert.Equal((status, code), (response.Status, response.Code));
        }
        await RefusedAsync(Sale(register, product, 10_00, 1, 10_00, null, redeemPoints: 50), HttpStatusCode.UnprocessableEntity, "pos.loyalty_customer_required");
        await RefusedAsync(Sale(register, product, 10_00, 1, 10_00, customer, redeemPoints: 19), HttpStatusCode.UnprocessableEntity, "pos.loyalty_min_points");
        await RefusedAsync(Sale(register, product, 10_00, 1, 10_00, customer, redeemPoints: 119), HttpStatusCode.Conflict, "pos.loyalty_insufficient_points");
        await RefusedAsync(Sale(register, product, 2_00, 1, 2_00, customer, redeemPoints: 41), HttpStatusCode.UnprocessableEntity, "pos.loyalty_exceeds_total"); // 2.05 > 2.00
        await RefusedAsync(Sale(register, product, 10_00, 1, 10_00, customer, redeemPoints: 0), HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Equal(118, await PointsAsync(shop, customer));

        // Redeem 100 points = 5.00 on two lines (19.00 + 1.00): 4.75 and 0.25. Total 15.00 instead of 20.00; cash pays 15.00.
        var other = await PosSupport.StockProductAsync(shop, 5, "Hülle");
        var key = PosSupport.Key();
        var body = new
        {
            registerId = register,
            lines = new[] { new { productId = product, quantity = 2, unitPriceMinor = 10_00, discountMinor = 1_00 }, new { productId = other, quantity = 1, unitPriceMinor = 1_00, discountMinor = 0 } },
            payments = new[] { new { method = "Cash", amountMinor = 20_00 } },
            customerId = customer,
            redeemPoints = 100,
        };
        var receipt = (await shop.PostAsync("/api/pos/sales", body, key)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((15_00L, 5_00L, 100, 5_00L), (receipt["totalGrossMinor"]!.GetValue<long>(), receipt["changeMinor"]!.GetValue<long>(),
            receipt["loyaltyPointsRedeemed"]!.GetValue<int>(), receipt["loyaltyDiscountMinor"]!.GetValue<long>()));
        var lines = receipt["lines"]!.AsArray();
        Assert.Equal([(4_75L, 14_25L, 1_00L), (25L, 75L, 0L)],
            lines.Select(l => (l!["loyaltyDiscountMinor"]!.GetValue<long>(), l["lineGrossMinor"]!.GetValue<long>(), l["discountMinor"]!.GetValue<long>())));
        // VAT on the reduced amounts, per line as always: 14.25 → 2.28 and 0.75 → 0.12.
        var vat = receipt["vat"]!.AsArray().Single()!;
        Assert.Equal((15_00L, 2_40L), (vat["grossMinor"]!.GetValue<long>(), vat["vatMinor"]!.GetValue<long>()));
        Assert.Equal([2_28L, 12L], lines.Select(l => l!["vatMinor"]!.GetValue<long>()));
        // Only cash is a payment; the points are not.
        Assert.Equal([("Cash", 15_00L)], receipt["payments"]!.AsArray().Select(p => (p!["method"]!.GetValue<string>(), p["amountMinor"]!.GetValue<long>())));
        // Earned on what was paid: 15 full euros × 2 = 30. Balance 118 − 100 + 30 = 48.
        Assert.Equal((30, 48), (receipt["loyaltyPointsEarned"]!.GetValue<int>(), receipt["loyaltyBalanceAfter"]!.GetValue<int>()));
        var notes = receipt["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Treuepunkte eingelöst: 100 (Preisnachlass 5,00 €)", notes);
        Assert.Contains("Punktestand: 48", notes);
        Assert.Equal(48, await PointsAsync(shop, customer));
        // A replay neither takes nor credits points again.
        var replay = (await shop.PostAsync("/api/pos/sales", body, key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((Id(receipt), 48), (Id(replay), replay["loyaltyBalanceAfter"]!.GetValue<int>()));
        Assert.Equal(48, await PointsAsync(shop, customer));
        // The database: receipt and lines are consistent.
        Assert.Equal(5_00L, await _pos.OwnerScalarAsync<long>($"SELECT sum(loyalty_discount_minor)::bigint FROM pos.receipt_lines WHERE receipt_id = '{Id(receipt)}'"));
        Assert.Equal(15_00L, await _pos.OwnerScalarAsync<long>($"SELECT sum(line_gross_minor)::bigint FROM pos.receipt_lines WHERE receipt_id = '{Id(receipt)}'"));

        // The history, newest first.
        var history = (await shop.GetAsync($"/api/pos/customers/{customer}/loyalty")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((true, 5, 20), (history["enabled"]!.GetValue<bool>(), history["pointValueMinor"]!.GetValue<int>(), history["minRedeemPoints"]!.GetValue<int>()));
        var types = history["transactions"]!["items"]!.AsArray().Select(t => (t!["type"]!.GetValue<string>(), t["points"]!.GetValue<int>(), t["balanceAfter"]!.GetValue<int>())).ToList();
        Assert.Equal(3, types.Count);
        Assert.Equal(("Earn", 118, 118), types[2]);
        Assert.Contains(("Redeem", -100, 18), types);
        Assert.Contains(("Earn", 30, 48), types);
        Assert.Equal(receipt["number"]!.GetValue<long>(), history["transactions"]!["items"]!.AsArray().First()!["receiptNumber"]!.GetValue<long>());

        // The Z-report shows the price reductions from points.
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(5_00, session["summary"]!["loyaltyDiscountMinor"]!.GetValue<long>());
    }

    [Fact]
    public async Task Redemption_is_not_the_cashiers_discount_and_gift_cards_neither_earn_nor_take_points()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 50);
        (await shop.PutAsync($"/api/pos/prices/{product}", new { priceMinor = 10_00 })).Expect(HttpStatusCode.OK);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var customer = await CustomerAsync(shop);
        await EnableAsync(shop, pointsPerEuro: 1, pointValueMinor: 10);
        (await shop.PostAsync($"/api/pos/customers/{customer}/loyalty/adjustments", new { points = 500, reason = "Startguthaben" })).Expect(HttpStatusCode.OK);

        // A cashier without the discount right (and without price override) redeems 50 points = 5.00 off a 10.00 item.
        var (staff, staffId) = await _pos.MemberWithIdAsync(shop, "Staff");
        (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = Array.Empty<string>(), maxDiscountPercent = 0 })).Expect(HttpStatusCode.OK);
        Assert.Equal("pos.discount_forbidden", (await staff.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1, 10_00, customer, discount: 1), PosSupport.Key())).Code);
        var reduced = (await staff.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1, 5_00, customer, redeemPoints: 50), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((5_00L, 5_00L, 0L), (reduced["totalGrossMinor"]!.GetValue<long>(), reduced["lines"]![0]!["loyaltyDiscountMinor"]!.GetValue<long>(),
            reduced["lines"]![0]!["discountMinor"]!.GetValue<long>()));
        Assert.Equal(500 - 50 + 5, reduced["loyaltyBalanceAfter"]!.GetValue<int>());

        // A gift card line: points reduce only the product (10.00), not the gift card (25.00), and the gift card earns nothing.
        var tooMuch = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new object[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 }, new { quantity = 1, giftCardAmountMinor = 25_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 35_00 } },
            customerId = customer,
            redeemPoints = 101, // 10.10 > 10.00 eligible
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.loyalty_exceeds_total", 10_00L), (tooMuch.Status, tooMuch.Code, tooMuch.Json["eligibleTotalMinor"]!.GetValue<long>()));
        var mixed = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new object[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 }, new { quantity = 1, giftCardAmountMinor = 25_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 35_00 } },
            customerId = customer,
            redeemPoints = 40,
        }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal([(4_00L, 6_00L), (0L, 25_00L)],
            mixed["lines"]!.AsArray().Select(l => (l!["loyaltyDiscountMinor"]!.GetValue<long>(), l["lineGrossMinor"]!.GetValue<long>())));
        Assert.Equal((31_00L, 6), (mixed["totalGrossMinor"]!.GetValue<long>(), mixed["loyaltyPointsEarned"]!.GetValue<int>())); // 6 euros eligible, not 31

        // Offline sales cannot redeem points.
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, offlineMode = true })).Expect(HttpStatusCode.OK);
        var offline = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 10_00 } }, customerId = customer, redeemPoints = 10,
            offline = new { recordedAt = DateTimeOffset.UtcNow.AddMinutes(-5), reference = "OFF-1" },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_payment_not_allowed"), (offline.Status, offline.Code));
        // … but an offline sale for a customer still earns when it is booked.
        var earnedOffline = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 10_00 } }, customerId = customer,
            offline = new { recordedAt = DateTimeOffset.UtcNow.AddMinutes(-5), reference = "OFF-2" },
        }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(10, earnedOffline["loyaltyPointsEarned"]!.GetValue<int>());
    }

    [Fact]
    public async Task Storno_and_returns_reverse_points()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 50);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var customer = await CustomerAsync(shop);
        await EnableAsync(shop, pointsPerEuro: 1, pointValueMinor: 1);
        (await shop.PostAsync($"/api/pos/customers/{customer}/loyalty/adjustments", new { points = 300, reason = "Startguthaben" })).Expect(HttpStatusCode.OK);

        // Sale: 3 × 10.00, redeem 300 points (3.00) → 27.00, earns 27. Balance 300 − 300 + 27 = 27.
        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 3, 27_00, customer, redeemPoints: 300), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((27_00L, 27, 27), (sale["totalGrossMinor"]!.GetValue<long>(), sale["loyaltyPointsEarned"]!.GetValue<int>(), sale["loyaltyBalanceAfter"]!.GetValue<int>()));

        // Return 1 of 3: refund is the reduced line gross (9.00); a third of the earned points (9) is taken; redeemed points stay redeemed.
        var returned = (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "defekt" }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((-9_00L, -9, 18, 0), (returned["totalGrossMinor"]!.GetValue<long>(), returned["loyaltyPointsEarned"]!.GetValue<int>(),
            returned["loyaltyBalanceAfter"]!.GetValue<int>(), returned["loyaltyPointsRedeemed"]!.GetValue<int>()));
        Assert.Equal((-1_00L, -1_00L), (returned["lines"]![0]!["loyaltyDiscountMinor"]!.GetValue<long>(), returned["loyaltyDiscountMinor"]!.GetValue<long>()));
        Assert.Contains("Treuepunkte abgezogen: 9", returned["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(18, await PointsAsync(shop, customer));

        // The customer spends 15 of the remaining 18 points elsewhere; the second return can only take what is left (3), not 9.
        (await shop.PostAsync($"/api/pos/customers/{customer}/loyalty/adjustments", new { points = -15, reason = "Prämie ausgegeben" })).Expect(HttpStatusCode.OK);
        var second = (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "defekt" }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((-3, 0), (second["loyaltyPointsEarned"]!.GetValue<int>(), second["loyaltyBalanceAfter"]!.GetValue<int>()));
        Assert.Equal(0, await PointsAsync(shop, customer));

        // Storno of another sale: redeemed points come back, earned points go.
        (await shop.PostAsync($"/api/pos/customers/{customer}/loyalty/adjustments", new { points = 200, reason = "Kulanz" })).Expect(HttpStatusCode.OK);
        var cancelled = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 2, 19_00, customer, redeemPoints: 100), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((19_00L, 19, 119), (cancelled["totalGrossMinor"]!.GetValue<long>(), cancelled["loyaltyPointsEarned"]!.GetValue<int>(), cancelled["loyaltyBalanceAfter"]!.GetValue<int>()));
        var storno = (await shop.PostAsync($"/api/pos/sales/{Id(cancelled)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((-19_00L, -100, -19, 200, -1_00L), (storno["totalGrossMinor"]!.GetValue<long>(), storno["loyaltyPointsRedeemed"]!.GetValue<int>(),
            storno["loyaltyPointsEarned"]!.GetValue<int>(), storno["loyaltyBalanceAfter"]!.GetValue<int>(), storno["loyaltyDiscountMinor"]!.GetValue<long>()));
        Assert.Equal(200, await PointsAsync(shop, customer));
        var stornoNotes = storno["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Treuepunkte zurückgebucht: 100", stornoNotes);
        Assert.Contains("Treuepunkte abgezogen: 19", stornoNotes);

        // Storno when the earned points are already gone: takes what is there and records the actual figure.
        var earner = await CustomerAsync(shop, "Max Punkte");
        var big = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 5, 50_00, earner), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(50, big["loyaltyPointsEarned"]!.GetValue<int>());
        (await shop.PostAsync($"/api/pos/customers/{earner}/loyalty/adjustments", new { points = -40, reason = "Prämie ausgegeben" })).Expect(HttpStatusCode.OK);
        var late = (await shop.PostAsync($"/api/pos/sales/{Id(big)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((-10, 0), (late["loyaltyPointsEarned"]!.GetValue<int>(), late["loyaltyBalanceAfter"]!.GetValue<int>()));
        var lastTransaction = (await shop.GetAsync($"/api/pos/customers/{earner}/loyalty")).Expect(HttpStatusCode.OK).Json["transactions"]!["items"]!.AsArray().First()!;
        Assert.Equal(("ReverseEarn", -10, 0), (lastTransaction["type"]!.GetValue<string>(), lastTransaction["points"]!.GetValue<int>(), lastTransaction["balanceAfter"]!.GetValue<int>()));

        // Reversal works even after the programme was switched off.
        var third = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1, 10_00, customer, redeemPoints: 100), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        await EnableAsync(shop, enabled: false);
        (await shop.PostAsync($"/api/pos/sales/{Id(third)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created);
        Assert.Equal(200, await PointsAsync(shop, customer));
    }

    [Fact]
    public async Task Managers_adjust_points_with_a_reason_and_never_below_zero()
    {
        var shop = await _pos.ShopAsync();
        var customer = await CustomerAsync(shop);
        var url = $"/api/pos/customers/{customer}/loyalty/adjustments";
        Assert.Equal(0, await PointsAsync(shop, customer));

        var added = (await shop.PostAsync(url, new { points = 150, reason = "Kulanz nach Reklamation" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(150, added["points"]!.GetValue<int>());
        var transaction = added["transactions"]!["items"]!.AsArray().Single()!;
        Assert.Equal(("Adjust", 150, 150, "Kulanz nach Reklamation"), (transaction["type"]!.GetValue<string>(), transaction["points"]!.GetValue<int>(),
            transaction["balanceAfter"]!.GetValue<int>(), transaction["reason"]!.GetValue<string>()));
        Assert.NotNull(transaction["createdByName"]);

        var tooMany = await shop.PostAsync(url, new { points = -151, reason = "Korrektur" });
        Assert.Equal((HttpStatusCode.Conflict, "pos.loyalty_insufficient_points", 150), (tooMany.Status, tooMany.Code, tooMany.Json["points"]!.GetValue<int>()));
        (await shop.PostAsync(url, new { points = -150, reason = "Korrektur" })).Expect(HttpStatusCode.OK);
        (await shop.PostAsync(url, new { points = 10, reason = "" })).Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("pos.loyalty_points", (await shop.PostAsync(url, new { points = 0, reason = "nichts" })).Code);
        Assert.Equal("pos.customer_not_found", (await shop.PostAsync($"/api/pos/customers/{Guid.NewGuid()}/loyalty/adjustments", new { points = 1, reason = "x" })).Code);
        Assert.Equal("pos.customer_not_found", (await shop.GetAsync($"/api/pos/customers/{Guid.NewGuid()}/loyalty")).Code);
        Assert.Equal(0, await PointsAsync(shop, customer));

        // Staff sees the balance and history but cannot adjust.
        var staff = await _pos.MemberAsync(shop, "Staff");
        Assert.Equal(2, (await staff.GetAsync($"/api/pos/customers/{customer}/loyalty?pageSize=10")).Expect(HttpStatusCode.OK).Json["transactions"]!["totalCount"]!.GetValue<int>());
        (await staff.PostAsync(url, new { points = 1000, reason = "für mich" })).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Points_never_go_below_zero_under_concurrency_and_the_database_guards_the_history()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 100);
        var registers = new List<Guid>();
        for (var i = 1; i <= 4; i++)
            registers.Add(await PosSupport.RegisterWithSessionAsync(shop, $"Kasse {i}"));
        var customer = await CustomerAsync(shop);
        // No points are earned (0 per euro), so only the redemptions move the balance.
        await EnableAsync(shop, pointsPerEuro: 0, pointValueMinor: 1);
        (await shop.PostAsync($"/api/pos/customers/{customer}/loyalty/adjustments", new { points = 250, reason = "Startguthaben" })).Expect(HttpStatusCode.OK);

        // 12 sales on four registers each redeem 100 of 250 points: exactly two can.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            shop.PostAsync("/api/pos/sales", Sale(registers[i % registers.Count], product, 10_00, 1, 9_00, customer, redeemPoints: 100), PosSupport.Key())));
        Assert.Equal(2, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created),
            r => Assert.True(r.Status == HttpStatusCode.Conflict && r.Code == "pos.loyalty_insufficient_points", $"{(int)r.Status} {r.Code} {r.Body?.ToJsonString()}"));
        Assert.Equal(50, await PointsAsync(shop, customer));
        Assert.Equal(50L, await _pos.OwnerScalarAsync<long>($"SELECT sum(points)::bigint FROM pos.loyalty_transactions WHERE customer_id = '{customer}'"));
        Assert.Equal(200L, await _pos.OwnerScalarAsync<long>($"SELECT sum(loyalty_points_redeemed)::bigint FROM pos.receipts WHERE customer_id = '{customer}'"));

        // The database itself: never negative, never changed without a transaction, history append-only, accounts never deleted.
        var account = $"organization_id = '{shop.OrganizationId}' AND customer_id = '{customer}'";
        Assert.Equal("23514", (await _pos.OwnerTryAsync($"UPDATE pos.loyalty_accounts SET points = -1 WHERE {account}"))?.SqlState);
        var withoutTransaction = await _pos.OwnerTryAsync($"UPDATE pos.loyalty_accounts SET points = 9999 WHERE {account}");
        Assert.Equal(("23514", "ck_loyalty_accounts_ledger"), (withoutTransaction?.SqlState, withoutTransaction?.ConstraintName));
        var withoutBalance = await _pos.OwnerTryAsync(
            $"INSERT INTO pos.loyalty_transactions (id, organization_id, customer_id, type, points, balance_after, created_at, created_by) "
            + $"VALUES (gen_random_uuid(), '{shop.OrganizationId}', '{customer}', 'Earn', 500, 550, now(), gen_random_uuid())");
        Assert.Equal(("23514", "ck_loyalty_accounts_ledger"), (withoutBalance?.SqlState, withoutBalance?.ConstraintName));
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.loyalty_transactions SET points = 1 WHERE customer_id = '{customer}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"DELETE FROM pos.loyalty_transactions WHERE customer_id = '{customer}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"DELETE FROM pos.loyalty_accounts WHERE {account}"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync("TRUNCATE pos.loyalty_transactions"))?.SqlState);
        Assert.Equal(50, await PointsAsync(shop, customer));
    }
}
