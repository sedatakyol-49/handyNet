using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Gift cards (ADR-0020) on real PostgreSQL: selling (code returned once, never stored), redeeming as a means of payment
/// under a row lock, storno and returns, management (void, reissue), and the database guarantees behind the balance.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed partial class PosGiftCardTests(HandyNetFixture fixture)
{
    private readonly PosSupport _pos = new(fixture);

    private static Guid Id(JsonNode node, string property = "id") => PosSupport.Id(node, property);

    [GeneratedRegex("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$")]
    private static partial Regex CodeFormat();

    /// <summary>Sells one gift card for cash and returns (card id, code, receipt).</summary>
    private static async Task<(Guid CardId, string Code, JsonNode Receipt)> SellCardAsync(ApiClient shop, Guid register, long amount, Guid? customerId = null)
    {
        var receipt = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new[] { new { quantity = 1, giftCardAmountMinor = amount } },
            payments = new[] { new { method = "Cash", amountMinor = amount } },
            customerId,
        }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        var issued = receipt["issuedGiftCards"]!.AsArray().Single()!;
        return (Id(issued), issued["code"]!.GetValue<string>(), receipt);
    }

    private static object PayWithCard(Guid register, Guid product, long price, string code, long giftAmount, long cash = 0, int quantity = 1)
    {
        var payments = new List<object> { new { method = "GiftCard", amountMinor = giftAmount, giftCardCode = code } };
        if (cash > 0)
            payments.Add(new { method = "Cash", amountMinor = cash });
        return new { registerId = register, lines = new[] { new { productId = product, quantity, unitPriceMinor = price } }, payments };
    }

    private static async Task<long> BalanceAsync(ApiClient shop, string code) =>
        (await shop.GetAsync($"/api/pos/gift-cards/lookup?code={code}")).Expect(HttpStatusCode.OK).Json["balanceMinor"]!.GetValue<long>();

    [Fact]
    public async Task Selling_a_gift_card_returns_the_code_once_and_never_stores_it()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 5);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var customer = Id((await shop.PostAsync("/api/pos/customers", new { name = "Erika Beispiel" })).Expect(HttpStatusCode.Created).Json);

        var key = PosSupport.Key();
        var body = new
        {
            registerId = register,
            lines = new object[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 }, new { quantity = 1, giftCardAmountMinor = 25_00, unitPriceMinor = 25_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 40_00 } },
            customerId = customer,
        };
        var receipt = (await shop.PostAsync("/api/pos/sales", body, key)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((35_00L, 5_00L), (receipt["totalGrossMinor"]!.GetValue<long>(), receipt["changeMinor"]!.GetValue<long>()));

        var issued = receipt["issuedGiftCards"]!.AsArray().Single()!;
        var code = issued["code"]!.GetValue<string>();
        Assert.Matches(CodeFormat(), code);
        Assert.Equal((25_00L, code[^4..]), (issued["amountMinor"]!.GetValue<long>(), issued["codeLast4"]!.GetValue<string>()));
        // 31 December of the issue year + 3.
        Assert.Equal($"{DateTime.UtcNow.AddHours(1).Year + 3}-12-31", issued["expiresOn"]!.GetValue<string>());

        // The line: no product, not taxable, no VAT; the document shows only the masked code.
        var line = receipt["lines"]!.AsArray()[1]!;
        Assert.Null(line["productId"]);
        Assert.Equal(("NotTaxable", 0L, 25_00L, Id(issued)), (line["taxTreatment"]!.GetValue<string>(), line["vatMinor"]!.GetValue<long>(),
            line["lineGrossMinor"]!.GetValue<long>(), Id(line, "giftCardId")));
        Assert.Equal($"Geschenkgutschein ••••-{code[^4..]}", line["description"]!.GetValue<string>());
        Assert.Single(receipt["vat"]!.AsArray()); // VAT only for the product
        Assert.Equal(10_00, receipt["vat"]![0]!["grossMinor"]!.GetValue<long>());
        Assert.DoesNotContain(code, receipt["document"]!.ToJsonString());
        Assert.DoesNotContain(code.Replace("-", ""), receipt["document"]!.ToJsonString());

        // Replay and later reads never show the code again.
        var replay = (await shop.PostAsync("/api/pos/sales", body, key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(receipt), Id(replay));
        Assert.Null(replay["issuedGiftCards"]!.AsArray().Single()!["code"]);
        Assert.Equal(Id(issued), Id(replay["issuedGiftCards"]!.AsArray().Single()!));
        var read = (await shop.GetAsync($"/api/pos/sales/{Id(receipt)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Null(read["issuedGiftCards"]!.AsArray().Single()!["code"]);
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.gift_cards WHERE organization_id = '{shop.OrganizationId}'"));

        // Nowhere in the database: only the hash and the last four characters.
        var compact = code.Replace("-", "");
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>(
            $"SELECT count(*) FROM pos.gift_cards WHERE id = '{Id(issued)}' AND code_hash = encode(sha256('{compact}'::bytea), 'hex') AND code_last4 = '{code[^4..]}'"));
        foreach (var needle in new[] { code, compact })
        {
            Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.gift_cards WHERE gift_cards::text LIKE '%{needle}%'"));
            Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.receipts r WHERE r::text LIKE '%{needle}%'"));
            Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.receipt_lines l WHERE l::text LIKE '%{needle}%'"));
            Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.gift_card_transactions t WHERE t::text LIKE '%{needle}%'"));
            Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM infra.audit_log a WHERE a::text LIKE '%{needle}%'"));
        }

        // Lookup at the till (any spelling of the code); an unknown or malformed code is a 404.
        var lookup = (await shop.GetAsync($"/api/pos/gift-cards/lookup?code={compact.ToLowerInvariant()}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((Id(issued), 25_00L, "Active", true, code[^4..]), (Id(lookup), lookup["balanceMinor"]!.GetValue<long>(), lookup["status"]!.GetValue<string>(),
            lookup["usable"]!.GetValue<bool>(), lookup["codeLast4"]!.GetValue<string>()));
        Assert.Null(lookup["code"]);
        foreach (var unknown in new[] { "ABCD-EFGH-JKLM-NPQR", "kein-code", "" })
        {
            var missing = await shop.GetAsync($"/api/pos/gift-cards/lookup?code={unknown}");
            Assert.Equal((HttpStatusCode.NotFound, "pos.gift_card_not_found"), (missing.Status, missing.Code));
        }

        // Z-report (live figures of the open session): the gift card sold.
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((1, 25_00L), (session["summary"]!["giftCardsSoldCount"]!.GetValue<int>(), session["summary"]!["giftCardsSoldMinor"]!.GetValue<long>()));
        var notTaxable = session["summary"]!["byTaxTreatment"]!.AsArray().Single(t => t!["treatment"]!.GetValue<string>() == "NotTaxable")!;
        Assert.Equal((25_00L, 0L), (notTaxable["grossMinor"]!.GetValue<long>(), notTaxable["vatMinor"]!.GetValue<long>()));

        // A receipt that sold a gift card cannot be invoiced.
        Assert.Equal("pos.invoice_gift_card", (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new
        {
            customer = new { name = "Erika Beispiel", street = "Weg 1", postalCode = "10115", city = "Berlin", countryCode = "DE" },
        })).Code);
    }

    [Fact]
    public async Task Gift_card_lines_are_validated()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 5);
        var register = await PosSupport.RegisterWithSessionAsync(shop);

        async Task<ApiResponse> AttemptAsync(object line, long pay) => await shop.PostAsync("/api/pos/sales",
            new { registerId = register, lines = new[] { line }, payments = new[] { new { method = "Cash", amountMinor = pay } } }, PosSupport.Key());

        Assert.Equal("pos.gift_card_amount", (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 4_99 }, 4_99)).Code);
        Assert.Equal("pos.gift_card_amount", (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 500_01 }, 500_01)).Code);
        Assert.Equal("pos.gift_card_line", (await AttemptAsync(new { quantity = 2, giftCardAmountMinor = 25_00 }, 50_00)).Code);
        Assert.Equal("pos.gift_card_line", (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 25_00, discountMinor = 1_00 }, 24_00)).Code);
        Assert.Equal("pos.gift_card_line", (await AttemptAsync(new { productId = product, quantity = 1, giftCardAmountMinor = 25_00 }, 25_00)).Code);
        Assert.Equal("pos.gift_card_line", (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 25_00, unitPriceMinor = 20_00 }, 25_00)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 0 }, 1)).Status);
        (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 5_00 }, 5_00)).Expect(HttpStatusCode.Created);
        (await AttemptAsync(new { quantity = 1, giftCardAmountMinor = 500_00, note = "Geburtstag" }, 500_00)).Expect(HttpStatusCode.Created);
        Assert.Equal(2L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.gift_cards WHERE organization_id = '{shop.OrganizationId}'"));
        Assert.Equal(3L, await _pos.OwnerScalarAsync<long>($"SELECT next_receipt_number FROM pos.registers WHERE id = '{register}'"));

        // Offline: neither selling a gift card nor paying with one.
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, offlineMode = true })).Expect(HttpStatusCode.OK);
        var offline = new { recordedAt = DateTimeOffset.UtcNow.AddMinutes(-5), reference = "OFF-1" };
        var sellOffline = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { quantity = 1, giftCardAmountMinor = 25_00 } }, payments = new[] { new { method = "Cash", amountMinor = 25_00 } }, offline,
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_gift_card_not_allowed"), (sellOffline.Status, sellOffline.Code));
        var payOffline = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "GiftCard", amountMinor = 10_00, giftCardCode = "ABCD-EFGH-JKLM-NPQR" } }, offline,
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_payment_not_allowed"), (payOffline.Status, payOffline.Code));
    }

    [Fact]
    public async Task Gift_card_pays_as_a_non_cash_method_and_storno_restores_it()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 30);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var (cardId, code, _) = await SellCardAsync(shop, register, 50_00);

        // Refused: unknown code, no code, more than the balance, more than the total, a gift card buying a gift card, a code on a cash payment.
        var unknown = await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, "ABCD-EFGH-JKLM-NPQR", 10_00), PosSupport.Key());
        Assert.Equal((HttpStatusCode.NotFound, "pos.gift_card_not_found"), (unknown.Status, unknown.Code));
        var noCode = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "GiftCard", amountMinor = 10_00 } },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.gift_card_payment"), (noCode.Status, noCode.Code));
        var tooMuch = await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, code, 60_00, quantity: 6), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_insufficient", 50_00L), (tooMuch.Status, tooMuch.Code, tooMuch.Json["balanceMinor"]!.GetValue<long>()));
        var overTotal = await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, code, 10_01), PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.gift_card_exceeds_total"), (overTotal.Status, overTotal.Code));
        var giftForGift = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { quantity = 1, giftCardAmountMinor = 20_00 } },
            payments = new[] { new { method = "GiftCard", amountMinor = 20_00, giftCardCode = code } },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.gift_card_exceeds_total"), (giftForGift.Status, giftForGift.Code));
        var codeOnCash = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 10_00, giftCardCode = code } },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.gift_card_payment"), (codeOnCash.Status, codeOnCash.Code));
        Assert.Equal(50_00, await BalanceAsync(shop, code)); // nothing was taken
        Assert.Equal(2L, await _pos.OwnerScalarAsync<long>($"SELECT next_receipt_number FROM pos.registers WHERE id = '{register}'"));

        // 3 × 10.00 = 30.00: 20.00 from the gift card, the rest in cash (15.00 tendered → 5.00 change).
        var key = PosSupport.Key();
        var sale = (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, code, 20_00, cash: 15_00, quantity: 3), key)).Expect(HttpStatusCode.Created).Json;
        var payments = sale["payments"]!.AsArray().ToDictionary(p => p!["method"]!.GetValue<string>(), p => p!["amountMinor"]!.GetValue<long>());
        Assert.Equal((20_00L, 10_00L, 5_00L), (payments["GiftCard"], payments["Cash"], sale["changeMinor"]!.GetValue<long>()));
        Assert.Empty(sale["issuedGiftCards"]!.AsArray());
        Assert.DoesNotContain(code, sale.ToJsonString()); // the receipt never shows the code of the card it was paid with
        Assert.Equal(30_00, await BalanceAsync(shop, code));
        // A replay does not charge the card again.
        (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, code, 20_00, cash: 15_00, quantity: 3), key)).Expect(HttpStatusCode.OK);
        Assert.Equal(30_00, await BalanceAsync(shop, code));

        // Expected cash counts only the cash part; the session shows the gift card as its own payment method.
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        var byMethod = session["summary"]!["byPaymentMethod"]!.AsArray().ToDictionary(p => p!["method"]!.GetValue<string>(), p => p!["amountMinor"]!.GetValue<long>());
        Assert.Equal((20_00L, 60_00L, 0L), (byMethod["GiftCard"], byMethod["Cash"], byMethod["Card"])); // 50.00 card sale + 10.00
        Assert.Equal(100_00 + 60_00, session["expectedCashMinor"]!.GetValue<long>());
        var report = (await shop.GetAsync($"/api/pos/reports/sales?sessionId={Id(session)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(20_00, report["byPaymentMethod"]!.AsArray().Single(m => m!["method"]!.GetValue<string>() == "GiftCard")!["amountMinor"]!.GetValue<long>());
        Assert.Single((await shop.GetAsync("/api/pos/sales?paymentMethod=GiftCard")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray());

        // The rest of the card, twice in one request (same code): added up against the balance.
        var twice = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 4, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "GiftCard", amountMinor = 20_00, giftCardCode = code }, new { method = "GiftCard", amountMinor = 20_00, giftCardCode = code.ToLowerInvariant() } },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_insufficient"), (twice.Status, twice.Code));

        // Storno of the sale: stock back, the 20.00 back on the card, the storno mirrors the gift card payment.
        var storno = (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created).Json;
        var mirrored = storno["payments"]!.AsArray().ToDictionary(p => p!["method"]!.GetValue<string>(), p => p!["amountMinor"]!.GetValue<long>());
        Assert.Equal((-20_00L, -10_00L), (mirrored["GiftCard"], mirrored["Cash"]));
        Assert.Equal(50_00, await BalanceAsync(shop, code));

        // The card's history: issue, redeem, restore — balance after each step.
        var detail = (await shop.GetAsync($"/api/pos/gift-cards/{cardId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal([("Issue", 50_00L, 50_00L), ("Redeem", -20_00L, 30_00L), ("Restore", 20_00L, 50_00L)],
            detail["transactions"]!.AsArray().Select(t => (t!["type"]!.GetValue<string>(), t["amountMinor"]!.GetValue<long>(), t["balanceAfterMinor"]!.GetValue<long>())));
        Assert.Equal(Id(storno), Id(detail["transactions"]!.AsArray().Last()!, "receiptId"));

        // Paid completely by gift card on a cash-rounding register: exact, no rounding.
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, cashRounding = true })).Expect(HttpStatusCode.OK);
        var exact = (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 9_99, code, 9_99), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(0, exact["roundingMinor"]!.GetValue<long>());
        Assert.Equal(40_01, await BalanceAsync(shop, code));
    }

    [Fact]
    public async Task Storno_of_the_issuing_receipt_voids_an_unused_card_only()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 10);
        var register = await PosSupport.RegisterWithSessionAsync(shop);

        // Unused: storno voids the card, the cash goes back.
        var (unusedId, unusedCode, unusedReceipt) = await SellCardAsync(shop, register, 25_00);
        var storno = (await shop.PostAsync($"/api/pos/sales/{Id(unusedReceipt)}/storno", new { reason = "Kunde hat es sich anders überlegt" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(-25_00, storno["totalGrossMinor"]!.GetValue<long>());
        var voided = (await shop.GetAsync($"/api/pos/gift-cards/lookup?code={unusedCode}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Voided", 0L, false), (voided["status"]!.GetValue<string>(), voided["balanceMinor"]!.GetValue<long>(), voided["usable"]!.GetValue<bool>()));
        var pay = await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, unusedCode, 10_00), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_not_usable"), (pay.Status, pay.Code));
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((0, 0L), (session["summary"]!["giftCardsSoldCount"]!.GetValue<int>(), session["summary"]!["giftCardsSoldMinor"]!.GetValue<long>()));
        Assert.Equal("Void", (await shop.GetAsync($"/api/pos/gift-cards/{unusedId}")).Expect(HttpStatusCode.OK).Json["transactions"]!.AsArray().Last()!["type"]!.GetValue<string>());

        // Partly redeemed: the issuing receipt cannot be cancelled any more, and the gift card line cannot be returned.
        var (_, usedCode, usedReceipt) = await SellCardAsync(shop, register, 25_00);
        var paid = (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, usedCode, 10_00), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        var refused = await shop.PostAsync($"/api/pos/sales/{Id(usedReceipt)}/storno", new { reason = "zu spät" });
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_already_redeemed"), (refused.Status, refused.Code));
        var returned = await shop.PostAsync($"/api/pos/sales/{Id(usedReceipt)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "zurück" }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_line_not_returnable"), (returned.Status, returned.Code));
        Assert.Equal(15_00, await BalanceAsync(shop, usedCode));
        // Once the paid sale is cancelled the card is whole again and its issuing receipt can be cancelled.
        (await shop.PostAsync($"/api/pos/sales/{Id(paid)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created);
        (await shop.PostAsync($"/api/pos/sales/{Id(usedReceipt)}/storno", new { reason = "doch zurück" })).Expect(HttpStatusCode.Created);
        Assert.Equal("Voided", (await shop.GetAsync($"/api/pos/gift-cards/lookup?code={usedCode}")).Expect(HttpStatusCode.OK).Json["status"]!.GetValue<string>());

        // A sale paid with a card that was voided meanwhile cannot be cancelled onto it.
        var (thirdId, thirdCode, _) = await SellCardAsync(shop, register, 25_00);
        var withThird = (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, thirdCode, 10_00), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        (await shop.PostAsync($"/api/pos/gift-cards/{thirdId}/void", new { reason = "Karte verloren" })).Expect(HttpStatusCode.OK);
        var onVoided = await shop.PostAsync($"/api/pos/sales/{Id(withThird)}/storno", new { reason = "Fehlbon" });
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_not_usable"), (onVoided.Status, onVoided.Code));
    }

    [Fact]
    public async Task Return_can_refund_onto_a_new_gift_card()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 10);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var sale = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 3, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 30_00 } },
        }, PosSupport.Key())).Expect(HttpStatusCode.Created).Json;

        var key = PosSupport.Key();
        var body = new { lines = new[] { new { lineNumber = 1, quantity = 2 } }, refundMethod = "GiftCard", reason = "Umtausch" };
        var returned = (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/returns", body, key)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("GiftCard", -20_00L), (returned["payments"]!.AsArray().Single()!["method"]!.GetValue<string>(), returned["payments"]!.AsArray().Single()!["amountMinor"]!.GetValue<long>()));
        var issued = returned["issuedGiftCards"]!.AsArray().Single()!;
        var code = issued["code"]!.GetValue<string>();
        Assert.Matches(CodeFormat(), code);
        Assert.Equal(20_00, issued["amountMinor"]!.GetValue<long>());
        // Replay: the same return receipt, no code, no second card.
        var replay = (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/returns", body, key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(returned), Id(replay));
        Assert.Null(replay["issuedGiftCards"]!.AsArray().Single()!["code"]);
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.gift_cards WHERE organization_id = '{shop.OrganizationId}'"));

        // No cash left the drawer; the new card pays.
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(100_00 + 30_00, session["expectedCashMinor"]!.GetValue<long>());
        Assert.Equal((0, 0L), (session["summary"]!["giftCardsSoldCount"]!.GetValue<int>(), session["summary"]!["giftCardsSoldMinor"]!.GetValue<long>()));
        Assert.Equal(20_00, await BalanceAsync(shop, code));
        (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, code, 10_00), PosSupport.Key())).Expect(HttpStatusCode.Created);
        Assert.Equal(10_00, await BalanceAsync(shop, code));
    }

    [Fact]
    public async Task A_gift_card_is_never_spent_twice_under_concurrency()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 100);
        // Several registers: sales on different registers do not queue on a register lock — only the gift card row serializes them.
        var registers = new List<Guid>();
        for (var i = 1; i <= 4; i++)
            registers.Add(await PosSupport.RegisterWithSessionAsync(shop, $"Kasse {i}"));
        var (cardId, code, _) = await SellCardAsync(shop, registers[0], 50_00);

        // 12 sales of 20.00 each against a balance of 50.00: exactly two can be paid.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            shop.PostAsync("/api/pos/sales", PayWithCard(registers[i % registers.Count], product, 20_00, code, 20_00), PosSupport.Key())));
        Assert.Equal(2, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created),
            r => Assert.True(r.Status == HttpStatusCode.Conflict && r.Code == "pos.gift_card_insufficient", $"{(int)r.Status} {r.Code} {r.Body?.ToJsonString()}"));
        Assert.Equal(10_00, await BalanceAsync(shop, code));
        Assert.Equal(10_00L, await _pos.OwnerScalarAsync<long>($"SELECT balance_minor FROM pos.gift_cards WHERE id = '{cardId}'"));
        Assert.Equal(10_00L, await _pos.OwnerScalarAsync<long>($"SELECT sum(amount_minor)::bigint FROM pos.gift_card_transactions WHERE gift_card_id = '{cardId}'"));
        Assert.Equal(40_00L, await _pos.OwnerScalarAsync<long>(
            $"SELECT sum(p.amount_minor)::bigint FROM pos.receipt_payments p WHERE p.method = 'GiftCard' AND p.organization_id = '{shop.OrganizationId}'"));
        // Stock: only the two paid sales took an item.
        Assert.Equal(98, (await shop.GetAsync("/api/inventory/stock?pageSize=200")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Single(i => Id(i!, "productId") == product)!["onHand"]!.GetValue<int>());

        // The exact remainder, raced again: one winner, never below zero.
        var last = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            shop.PostAsync("/api/pos/sales", PayWithCard(registers[i % registers.Count], product, 10_00, code, 10_00), PosSupport.Key())));
        Assert.Single(last, r => r.Status == HttpStatusCode.Created);
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT balance_minor FROM pos.gift_cards WHERE id = '{cardId}'"));
    }

    [Fact]
    public async Task The_database_guards_the_balance_and_the_transaction_history()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var (cardId, _, _) = await SellCardAsync(shop, register, 50_00);

        // Never negative, never above the initial amount (CHECK) …
        Assert.Equal("23514", (await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET balance_minor = -1 WHERE id = '{cardId}'"))?.SqlState);
        Assert.Equal("23514", (await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET balance_minor = 50_01 WHERE id = '{cardId}'"))?.SqlState);
        // … never changed without a transaction, and no transaction without the balance moving (checked at commit) …
        var withoutTransaction = await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET balance_minor = 10_00 WHERE id = '{cardId}'");
        Assert.Equal(("23514", "ck_gift_cards_ledger"), (withoutTransaction?.SqlState, withoutTransaction?.ConstraintName));
        var withoutBalance = await _pos.OwnerTryAsync(
            $"INSERT INTO pos.gift_card_transactions (id, organization_id, gift_card_id, type, amount_minor, balance_after_minor, created_at, created_by) "
            + $"VALUES (gen_random_uuid(), '{shop.OrganizationId}', '{cardId}', 'Restore', 10_00, 60_00, now(), gen_random_uuid())");
        Assert.Equal(("23514", "ck_gift_cards_ledger"), (withoutBalance?.SqlState, withoutBalance?.ConstraintName));
        // … the initial amount, issue data and expiry never change; cards and transactions are never deleted or rewritten.
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET initial_minor = 500_00 WHERE id = '{cardId}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET expires_on = '2099-12-31' WHERE id = '{cardId}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"DELETE FROM pos.gift_cards WHERE id = '{cardId}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.gift_card_transactions SET amount_minor = 1 WHERE gift_card_id = '{cardId}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"DELETE FROM pos.gift_card_transactions WHERE gift_card_id = '{cardId}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync("TRUNCATE pos.gift_card_transactions"))?.SqlState);
        Assert.Equal(50_00L, await _pos.OwnerScalarAsync<long>($"SELECT balance_minor FROM pos.gift_cards WHERE id = '{cardId}'"));

        // A voided card stays voided.
        (await shop.PostAsync($"/api/pos/gift-cards/{cardId}/void", new { reason = "Test" })).Expect(HttpStatusCode.OK);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET status = 'Active' WHERE id = '{cardId}'"))?.SqlState);
    }

    [Fact]
    public async Task Managers_list_void_and_reissue_cards_and_expired_cards_cannot_pay()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 10);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var customer = Id((await shop.PostAsync("/api/pos/customers", new { name = "Erika Beispiel" })).Expect(HttpStatusCode.Created).Json);
        var (firstId, firstCode, firstReceipt) = await SellCardAsync(shop, register, 50_00, customer);
        var (secondId, secondCode, _) = await SellCardAsync(shop, register, 20_00);

        var list = (await shop.GetAsync("/api/pos/gift-cards")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, list["totalCount"]!.GetValue<int>());
        Assert.Equal([secondId, firstId], list["items"]!.AsArray().Select(i => Id(i!))); // newest first
        var first = list["items"]!.AsArray().Single(i => Id(i!) == firstId)!;
        Assert.Equal((50_00L, 50_00L, "Active", "Erika Beispiel", firstReceipt["number"]!.GetValue<long>(), false),
            (first["initialMinor"]!.GetValue<long>(), first["balanceMinor"]!.GetValue<long>(), first["status"]!.GetValue<string>(), first["customerName"]!.GetValue<string>(),
                first["issuedReceiptNumber"]!.GetValue<long>(), first["expired"]!.GetValue<bool>()));
        Assert.Null(first["code"]);
        Assert.Null(first["codeHash"]);
        Assert.Equal([firstId], (await shop.GetAsync($"/api/pos/gift-cards?search={firstCode[^4..]}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Select(i => Id(i!)));
        Assert.Equal([secondId], (await shop.GetAsync($"/api/pos/gift-cards?search={secondCode}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Select(i => Id(i!)));
        Assert.Equal("pos.gift_card_status", (await shop.GetAsync("/api/pos/gift-cards?status=Nope")).Code);

        // Reissue: a new code for the same balance; the old code is dead.
        (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, firstCode, 10_00), PosSupport.Key())).Expect(HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync($"/api/pos/gift-cards/{firstId}/reissue", new { reason = "" })).Status);
        var reissued = (await shop.PostAsync($"/api/pos/gift-cards/{firstId}/reissue", new { reason = "Code fotografiert" })).Expect(HttpStatusCode.OK).Json;
        var newCode = reissued["code"]!.GetValue<string>();
        Assert.Matches(CodeFormat(), newCode);
        Assert.NotEqual(firstCode, newCode);
        Assert.Equal((firstId, 40_00L, newCode[^4..]), (Id(reissued["card"]!), reissued["card"]!["balanceMinor"]!.GetValue<long>(), reissued["card"]!["codeLast4"]!.GetValue<string>()));
        Assert.Equal("pos.gift_card_not_found", (await shop.GetAsync($"/api/pos/gift-cards/lookup?code={firstCode}")).Code);
        Assert.Equal("pos.gift_card_not_found", (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, firstCode, 10_00), PosSupport.Key())).Code);
        Assert.Equal(40_00, await BalanceAsync(shop, newCode));
        (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, newCode, 10_00), PosSupport.Key())).Expect(HttpStatusCode.Created);
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM infra.audit_log a WHERE a::text LIKE '%{newCode}%' OR a::text LIKE '%{newCode.Replace("-", "")}%'"));

        // Void: balance written off, never usable again, cannot be voided or reissued twice.
        var voided = (await shop.PostAsync($"/api/pos/gift-cards/{secondId}/void", new { reason = "Karte verloren" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Voided", 0L), (voided["status"]!.GetValue<string>(), voided["balanceMinor"]!.GetValue<long>()));
        Assert.Equal(("Void", -20_00L, "Karte verloren"), (voided["transactions"]!.AsArray().Last()!["type"]!.GetValue<string>(),
            voided["transactions"]!.AsArray().Last()!["amountMinor"]!.GetValue<long>(), voided["transactions"]!.AsArray().Last()!["reason"]!.GetValue<string>()));
        Assert.Equal("pos.gift_card_not_usable", (await shop.PostAsync($"/api/pos/gift-cards/{secondId}/void", new { reason = "nochmal" })).Code);
        Assert.Equal("pos.gift_card_not_usable", (await shop.PostAsync($"/api/pos/gift-cards/{secondId}/reissue", new { reason = "neu" })).Code);
        Assert.Equal("pos.gift_card_not_usable", (await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, secondCode, 10_00), PosSupport.Key())).Code);
        Assert.Equal([secondId], (await shop.GetAsync("/api/pos/gift-cards?status=Voided")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Select(i => Id(i!)));

        // Expired (the expiry is moved into the past as the owner, with the guard off for this statement): shown as not usable, cannot pay.
        Assert.Null(await _pos.OwnerTryAsync("ALTER TABLE pos.gift_cards DISABLE TRIGGER gift_cards_guard"));
        Assert.Null(await _pos.OwnerTryAsync($"UPDATE pos.gift_cards SET expires_on = current_date - 2 WHERE id = '{firstId}'"));
        Assert.Null(await _pos.OwnerTryAsync("ALTER TABLE pos.gift_cards ENABLE TRIGGER gift_cards_guard"));
        var expired = (await shop.GetAsync($"/api/pos/gift-cards/lookup?code={newCode}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Active", false, 30_00L), (expired["status"]!.GetValue<string>(), expired["usable"]!.GetValue<bool>(), expired["balanceMinor"]!.GetValue<long>()));
        var pay = await shop.PostAsync("/api/pos/sales", PayWithCard(register, product, 10_00, newCode, 10_00), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.gift_card_not_usable"), (pay.Status, pay.Code));
        Assert.True((await shop.GetAsync($"/api/pos/gift-cards/{firstId}")).Expect(HttpStatusCode.OK).Json["expired"]!.GetValue<bool>());

        // Staff: lookup and selling/paying at the till, but no list, detail, void or reissue.
        var staff = await _pos.MemberAsync(shop, "Staff");
        (await staff.GetAsync($"/api/pos/gift-cards/lookup?code={newCode}")).Expect(HttpStatusCode.OK);
        (await staff.GetAsync("/api/pos/gift-cards")).Expect(HttpStatusCode.Forbidden);
        (await staff.GetAsync($"/api/pos/gift-cards/{firstId}")).Expect(HttpStatusCode.Forbidden);
        (await staff.PostAsync($"/api/pos/gift-cards/{firstId}/void", new { reason = "x" })).Expect(HttpStatusCode.Forbidden);
        (await staff.PostAsync($"/api/pos/gift-cards/{firstId}/reissue", new { reason = "x" })).Expect(HttpStatusCode.Forbidden);
        await SellCardAsync(staff, register, 10_00);
    }
}
