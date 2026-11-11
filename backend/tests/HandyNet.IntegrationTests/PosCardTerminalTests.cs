using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Card terminals (ADR-0021) on real PostgreSQL: terminal profiles per register, card transactions (pending → result →
/// linked or reversed), their linkage to sales and returns, immutability of final results and the Production gate of the
/// simulated terminal.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosCardTerminalTests(HandyNetFixture fixture)
{
    private readonly PosSupport _pos = new(fixture);

    private static Guid Id(JsonNode node, string property = "id") => PosSupport.Id(node, property);

    private static readonly object Approved = new
    {
        status = "Approved", terminalId = "60012345", traceNumber = "000123", terminalReceiptNumber = "0042", cardName = "girocard",
        maskedPan = "672510******1234", authCode = "A1B2C3", resultCode = "00", resultText = "Zahlung erfolgt",
        customerReceiptText = "Kundenbeleg\nBetrag 10,00 EUR", merchantReceiptText = "Händlerbeleg",
    };

    private static async Task<Guid> ApprovedTransactionAsync(ApiClient shop, Guid register, long amount, string kind = "Payment", Guid? profile = null)
    {
        var started = (await shop.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind, amountMinor = amount, terminalProfileId = profile }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Pending", started["status"]!.GetValue<string>());
        (await shop.PostAsync($"/api/pos/card-transactions/{Id(started)}/result", Approved)).Expect(HttpStatusCode.OK);
        return Id(started);
    }

    private static object Sale(Guid register, Guid product, long cardAmount, Guid? cardTransactionId, long cash = 0, int quantity = 1)
    {
        var payments = new List<object> { new { method = "Card", amountMinor = cardAmount, cardTransactionId } };
        if (cash > 0)
            payments.Add(new { method = "Cash", amountMinor = cash });
        return new { registerId = register, lines = new[] { new { productId = product, quantity, unitPriceMinor = 10_00 } }, payments };
    }

    [Fact]
    public async Task Terminal_profiles_are_created_listed_updated_made_default_and_deleted()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var url = $"/api/pos/registers/{register}/terminals";
        Assert.Empty((await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray());

        // The first profile becomes the default; ZVT defaults: port 20007, password 000000.
        var zvt = (await shop.PostAsync(url, new { name = "Theke ZVT", connection = "ZvtBridge", host = "192.168.1.60", bridgeUrl = "http://localhost:9123" }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.True(zvt["isDefault"]!.GetValue<bool>());
        Assert.Equal((20007, "000000"), (zvt["port"]!.GetValue<int>(), zvt["zvtPassword"]!.GetValue<string>()));
        // Fields of other connection types are dropped.
        var manual = (await shop.PostAsync(url, new { name = "Manuell", connection = "Manual", host = "192.168.1.61", port = 20007, bridgeUrl = "http://localhost:9123", zvtPassword = "123456" }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.False(manual["isDefault"]!.GetValue<bool>());
        Assert.Null(manual["host"]);
        Assert.Null(manual["zvtPassword"]);
        var simulated = (await shop.PostAsync(url, new { name = "Simulation", connection = "Simulated" })).Expect(HttpStatusCode.Created).Json;

        Assert.Equal(["Theke ZVT", "Manuell", "Simulation"],
            (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal("Simulated", (await shop.GetAsync($"/api/pos/terminals/{Id(simulated)}")).Expect(HttpStatusCode.OK).Json["connection"]!.GetValue<string>());

        // Validation, duplicate name, default switch.
        var noHost = await shop.PostAsync(url, new { name = "Ohne Host", connection = "ZvtBridge", bridgeUrl = "http://localhost:9123" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.terminal_host"), (noHost.Status, noHost.Code));
        var duplicate = await shop.PostAsync(url, new { name = "Manuell", connection = "Manual" });
        Assert.Equal((HttpStatusCode.Conflict, "pos.terminal_name_taken"), (duplicate.Status, duplicate.Code));
        (await shop.PostAsync($"/api/pos/terminals/{Id(simulated)}/default")).Expect(HttpStatusCode.OK);
        var list = (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal("Simulation", list.Single(p => p!["isDefault"]!.GetValue<bool>())!["name"]!.GetValue<string>());

        // Update needs the version (optimistic concurrency); a stale version is refused.
        var current = (await shop.GetAsync($"/api/pos/terminals/{Id(zvt)}")).Expect(HttpStatusCode.OK).Json;
        var version = current["version"]!.GetValue<uint>();
        var withoutVersion = await shop.PutAsync($"/api/pos/terminals/{Id(zvt)}", new { name = "Theke ZVT", connection = "ZvtBridge", host = "192.168.1.60", bridgeUrl = "http://localhost:9123" });
        Assert.Equal("pos.terminal_version", withoutVersion.Code);
        var updated = (await shop.PutAsync($"/api/pos/terminals/{Id(zvt)}", new
        {
            name = "Theke ZVT", connection = "ZvtBridge", host = "terminal.shop.lan", port = 22000, bridgeUrl = "http://localhost:9123", zvtPassword = "654321", version,
        })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("terminal.shop.lan", 22000, "654321"), (updated["host"]!.GetValue<string>(), updated["port"]!.GetValue<int>(), updated["zvtPassword"]!.GetValue<string>()));
        var stale = await shop.PutAsync($"/api/pos/terminals/{Id(zvt)}", new { name = "X", connection = "Manual", version });
        Assert.Equal((HttpStatusCode.Conflict, "concurrency"), (stale.Status, stale.Code));

        // Deleting the default promotes the remaining profile with the first name; the audit log never holds the ZVT password.
        (await PosSupport.DeleteAsync(shop, $"/api/pos/terminals/{Id(simulated)}")).Expect(HttpStatusCode.NoContent);
        list = (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal("Manuell", list.Single(p => p!["isDefault"]!.GetValue<bool>())!["name"]!.GetValue<string>());
        (await shop.GetAsync($"/api/pos/terminals/{Id(simulated)}")).Expect(HttpStatusCode.NotFound);
        Assert.True(await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'TerminalProfile' AND entity_id = '{Id(zvt)}'") >= 2);
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>(
            $"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'TerminalProfile' AND (changes::text LIKE '%654321%' OR changes::text LIKE '%000000%')"));
    }

    [Fact]
    public async Task Staff_reads_terminals_and_runs_card_transactions_but_only_managers_configure()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var profile = Id((await shop.PostAsync($"/api/pos/registers/{register}/terminals", new { name = "Simulation", connection = "Simulated" })).Expect(HttpStatusCode.Created).Json);
        var staff = await _pos.MemberAsync(shop, "Staff");

        Assert.Single((await staff.GetAsync($"/api/pos/registers/{register}/terminals")).Expect(HttpStatusCode.OK).Json.AsArray());
        (await staff.PostAsync($"/api/pos/registers/{register}/terminals", new { name = "X", connection = "Manual" })).Expect(HttpStatusCode.Forbidden);
        (await staff.PostAsync($"/api/pos/terminals/{profile}/default")).Expect(HttpStatusCode.Forbidden);
        (await PosSupport.DeleteAsync(staff, $"/api/pos/terminals/{profile}")).Expect(HttpStatusCode.Forbidden);

        var transaction = await ApprovedTransactionAsync(staff, register, 10_00, profile: profile);
        var read = (await staff.GetAsync($"/api/pos/card-transactions/{transaction}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Approved", true, "Simulation"), (read["status"]!.GetValue<string>(), read["isTest"]!.GetValue<bool>(), read["terminalName"]!.GetValue<string>()));

        // A profile of another register is not accepted.
        var other = await PosSupport.RegisterAsync(shop, "Kasse 2");
        var foreign = await staff.PostAsync($"/api/pos/registers/{other}/card-transactions", new { kind = "Payment", amountMinor = 10_00, terminalProfileId = profile });
        Assert.Equal((HttpStatusCode.NotFound, "pos.terminal_not_found"), (foreign.Status, foreign.Code));
    }

    [Fact]
    public async Task Card_transaction_results_are_final_and_a_full_card_number_is_refused()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var url = $"/api/pos/registers/{register}/card-transactions";
        var started = (await shop.PostAsync(url, new { kind = "Payment", amountMinor = 25_00 })).Expect(HttpStatusCode.Created).Json;
        var id = Id(started);
        Assert.Equal(("Pending", "Payment", 25_00L), (started["status"]!.GetValue<string>(), started["kind"]!.GetValue<string>(), started["amountMinor"]!.GetValue<long>()));

        var fullPan = await shop.PostAsync($"/api/pos/card-transactions/{id}/result", new { status = "Approved", maskedPan = "4111111111111111" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.card_pan_not_masked"), (fullPan.Status, fullPan.Code));
        var pending = await shop.PostAsync($"/api/pos/card-transactions/{id}/result", new { status = "Pending" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.card_transaction_status"), (pending.Status, pending.Code));
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.card_transactions WHERE id = '{id}' AND masked_pan IS NOT NULL"));

        var declined = (await shop.PostAsync($"/api/pos/card-transactions/{id}/result", new { status = "Declined", resultCode = "05", resultText = "Karte abgelehnt" }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Declined", "05"), (declined["status"]!.GetValue<string>(), declined["resultCode"]!.GetValue<string>()));
        Assert.NotNull(declined["completedAt"]);

        // Final: a repeated identical report is accepted without change, another result is refused; a declined charge cannot be reversed.
        (await shop.PostAsync($"/api/pos/card-transactions/{id}/result", new { status = "Declined", resultText = "anders" })).Expect(HttpStatusCode.OK);
        Assert.Equal("Karte abgelehnt", (await shop.GetAsync($"/api/pos/card-transactions/{id}")).Expect(HttpStatusCode.OK).Json["resultText"]!.GetValue<string>());
        var changed = await shop.PostAsync($"/api/pos/card-transactions/{id}/result", Approved);
        Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_final"), (changed.Status, changed.Code));
        var reversal = await shop.PostAsync($"/api/pos/card-transactions/{id}/reversal", new { });
        Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_not_approved"), (reversal.Status, reversal.Code));

        // Reversal of an approved, unlinked charge.
        var approved = await ApprovedTransactionAsync(shop, register, 30_00);
        var reversed = (await shop.PostAsync($"/api/pos/card-transactions/{approved}/reversal", new { resultText = "Verkauf fehlgeschlagen" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Reversed", "Verkauf fehlgeschlagen"), (reversed["status"]!.GetValue<string>(), reversed["reversalText"]!.GetValue<string>()));

        // Lists: by status and "without a receipt".
        var third = await ApprovedTransactionAsync(shop, register, 40_00);
        var unlinked = (await shop.GetAsync($"/api/pos/card-transactions?registerId={register}&status=Approved&unlinked=true")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal([third], unlinked["items"]!.AsArray().Select(i => Id(i!)));
        Assert.Equal(3, (await shop.GetAsync($"/api/pos/card-transactions?registerId={register}")).Expect(HttpStatusCode.OK).Json["totalCount"]!.GetValue<int>());
        Assert.Equal("pos.card_transaction_status", (await shop.GetAsync("/api/pos/card-transactions?status=Nope")).Code);

        // The database refuses any change of a final row, a link of a non-approved one and every delete — also for the owner.
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.card_transactions SET status = 'Approved' WHERE id = '{id}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.card_transactions SET amount_minor = 1 WHERE id = '{third}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.card_transactions SET auth_code = 'X' WHERE id = '{third}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.card_transactions SET status = 'Approved', reversed_at = NULL WHERE id = '{approved}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"DELETE FROM pos.card_transactions WHERE id = '{third}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync("TRUNCATE pos.card_transactions"))?.SqlState);
    }

    [Fact]
    public async Task Sale_links_the_card_transaction_and_prints_the_terminal_data()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 20);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var other = await PosSupport.RegisterWithSessionAsync(shop, "Kasse 2");

        // Refused: wrong amount, another register's transaction, a pending or reversed one, an unknown id, a cash payment naming it.
        var twenty = await ApprovedTransactionAsync(shop, register, 20_00);
        async Task RefusedAsync(object body, string reason)
        {
            var response = await shop.PostAsync("/api/pos/sales", body, PosSupport.Key());
            Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_invalid"), (response.Status, response.Code));
            Assert.Equal(reason, response.Json["details"]?["reason"]?.GetValue<string>() ?? response.Json["reason"]?.GetValue<string>());
        }
        await RefusedAsync(Sale(register, product, 10_00, twenty), "amount");
        await RefusedAsync(Sale(other, product, 20_00, twenty, quantity: 2), "register");
        var pendingId = Id((await shop.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Payment", amountMinor = 10_00 })).Expect(HttpStatusCode.Created).Json);
        await RefusedAsync(Sale(register, product, 10_00, pendingId), "status");
        var reversed = await ApprovedTransactionAsync(shop, register, 10_00);
        (await shop.PostAsync($"/api/pos/card-transactions/{reversed}/reversal", new { })).Expect(HttpStatusCode.OK);
        await RefusedAsync(Sale(register, product, 10_00, reversed), "status");
        await RefusedAsync(Sale(register, product, 10_00, Guid.NewGuid()), "not_found");
        var refund = await ApprovedTransactionAsync(shop, register, 10_00, "Refund");
        await RefusedAsync(Sale(register, product, 10_00, refund), "kind");
        var onCash = await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Cash", amountMinor = 10_00, cardTransactionId = twenty } },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.card_transaction_payment"), (onCash.Status, onCash.Code));
        // Nothing was booked and no number used.
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.receipts WHERE register_id = '{register}'"));

        // Split payment: 20.00 by card (terminal) + 10.00 cash for three items.
        var key = PosSupport.Key();
        var receipt = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, twenty, cash: 10_00, quantity: 3), key)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(1, receipt["number"]!.GetValue<long>());
        var cardPayment = receipt["payments"]!.AsArray().Single(p => p!["method"]!.GetValue<string>() == "Card")!;
        Assert.Equal(("girocard", "672510******1234", "000123", "A1B2C3", "60012345", "0042"),
            (cardPayment["card"]!["cardName"]!.GetValue<string>(), cardPayment["card"]!["maskedPan"]!.GetValue<string>(), cardPayment["card"]!["traceNumber"]!.GetValue<string>(),
                cardPayment["card"]!["authCode"]!.GetValue<string>(), cardPayment["card"]!["terminalId"]!.GetValue<string>(),
                cardPayment["card"]!["terminalReceiptNumber"]!.GetValue<string>()));
        Assert.Null(receipt["payments"]!.AsArray().Single(p => p!["method"]!.GetValue<string>() == "Cash")!["card"]);
        var printed = receipt["document"]!["payments"]!.AsArray().Single(p => p!["method"]!.GetValue<string>() == "Card")!["card"]!;
        Assert.Equal("000123", printed["traceNumber"]!.GetValue<string>());

        var linked = (await shop.GetAsync($"/api/pos/card-transactions/{twenty}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((Id(receipt), 1L), (Id(linked, "receiptId"), linked["receiptNumber"]!.GetValue<long>()));
        Assert.NotNull(linked["linkedAt"]);

        // Replay returns the same receipt with the terminal data; a second sale cannot use the transaction; it cannot be reversed any more.
        var replay = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, twenty, cash: 10_00, quantity: 3), key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(receipt), Id(replay));
        Assert.Equal("000123", replay["payments"]!.AsArray().Single(p => p!["method"]!.GetValue<string>() == "Card")!["card"]!["traceNumber"]!.GetValue<string>());
        await RefusedAsync(Sale(register, product, 20_00, twenty, quantity: 2), "linked");
        var reversal = await shop.PostAsync($"/api/pos/card-transactions/{twenty}/reversal", new { });
        Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_linked"), (reversal.Status, reversal.Code));
        // The database agrees: a linked transaction cannot be moved to another receipt or reversed, even by the owner.
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.card_transactions SET receipt_id = NULL, linked_at = NULL WHERE id = '{twenty}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.card_transactions SET status = 'Reversed', reversed_at = now() WHERE id = '{twenty}'"))?.SqlState);

        // Without an id the card payment is "confirmed by the cashier" exactly as before.
        var manual = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, null), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Null(manual["payments"]!.AsArray().Single()!["card"]);
        Assert.Null(manual["document"]!["payments"]!.AsArray().Single()!["card"]);

        // Return with a card refund settled on the terminal.
        var refundOfTen = await ApprovedTransactionAsync(shop, register, 10_00, "Refund");
        var wrongRefund = await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 2 } }, refundMethod = "Card", reason = "defekt", cardTransactionId = refundOfTen }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_invalid"), (wrongRefund.Status, wrongRefund.Code));
        var cashWithCard = await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "defekt", cardTransactionId = refundOfTen }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.card_transaction_payment"), (cashWithCard.Status, cashWithCard.Code));
        var paymentAsRefund = await ApprovedTransactionAsync(shop, register, 10_00);
        var wrongKind = await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Card", reason = "defekt", cardTransactionId = paymentAsRefund }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_invalid"), (wrongKind.Status, wrongKind.Code));
        var returned = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Card", reason = "defekt", cardTransactionId = refundOfTen }, PosSupport.Key()))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal(-10_00, returned["payments"]!.AsArray().Single()!["amountMinor"]!.GetValue<long>());
        Assert.Equal("000123", returned["payments"]!.AsArray().Single()!["card"]!["traceNumber"]!.GetValue<string>());
        Assert.Equal(Id(returned), Id((await shop.GetAsync($"/api/pos/card-transactions/{refundOfTen}")).Expect(HttpStatusCode.OK).Json, "receiptId"));
    }

    [Fact]
    public async Task A_card_transaction_settles_at_most_one_receipt_under_concurrency()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 50);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var transaction = await ApprovedTransactionAsync(shop, register, 10_00);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, transaction), PosSupport.Key())));
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created),
            r => Assert.True(r.Status == HttpStatusCode.Conflict && r.Code == "pos.card_transaction_invalid", $"{(int)r.Status} {r.Code} {r.Body?.ToJsonString()}"));
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.receipts WHERE register_id = '{register}'"));
        Assert.Equal(2L, await _pos.OwnerScalarAsync<long>($"SELECT next_receipt_number FROM pos.registers WHERE id = '{register}'"));
        var winner = Id(results.Single(r => r.Status == HttpStatusCode.Created).Json);
        Assert.Equal(winner, await _pos.OwnerScalarAsync<Guid>($"SELECT receipt_id FROM pos.card_transactions WHERE id = '{transaction}'"));
    }

    [Fact]
    public async Task Staff_without_the_refund_right_cannot_start_a_terminal_refund()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var (staff, staffId) = await _pos.MemberWithIdAsync(shop, "Staff");
        // Staff holds pos.refund by default; take it away.
        (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = new[] { "pos.discount" }, maxDiscountPercent = 10 })).Expect(HttpStatusCode.OK);

        var refused = await staff.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Refund", amountMinor = 10_00 });
        Assert.Equal((HttpStatusCode.Forbidden, "permission.denied"), (refused.Status, refused.Code));
        (await staff.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Payment", amountMinor = 10_00 })).Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Production_refuses_the_simulated_terminal_unless_staging_allows_it()
    {
        // A shop with a manager whose credentials the test knows, a register and a simulated profile created outside Production.
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var existing = Id((await shop.PostAsync($"/api/pos/registers/{register}/terminals", new { name = "Simulation", connection = "Simulated" })).Expect(HttpStatusCode.Created).Json);
        var email = $"prod-{Scenario.UniqueSuffix()}@test.de";
        (await shop.PostAsync("/api/organization/staff", new { email, displayName = "Leitung", password = PosSupport.StaffPassword, role = "Manager" })).Expect(HttpStatusCode.OK);

        WebApplicationFactory<Program> Production(bool allow) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Production");
            web.UseSetting("ConnectionStrings:HandyNet", fixture.AppConnectionString);
            web.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            web.UseSetting("FileStorage:Provider", "Local");
            web.UseSetting("FileStorage:Local:RootPath", fixture.FileRoot);
            if (allow)
                web.UseSetting("Pos:Terminal:AllowSimulatedInProduction", "true");
            web.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

        async Task<ApiClient> ManagerAsync(WebApplicationFactory<Program> factory)
        {
            var client = new ApiClient(factory.CreateClient());
            await client.LoginAsync(email, PosSupport.StaffPassword);
            client.OrganizationId = shop.OrganizationId;
            return client;
        }

        await using (var production = Production(allow: false))
        {
            var manager = await ManagerAsync(production);
            var url = $"/api/pos/registers/{register}/terminals";
            var created = await manager.PostAsync(url, new { name = "Sim 2", connection = "Simulated" });
            Assert.Equal((HttpStatusCode.Conflict, "pos.terminal_simulated_not_allowed"), (created.Status, created.Code));
            // Changing an existing profile into a simulated one is refused as well …
            var manual = (await manager.PostAsync(url, new { name = "Manuell", connection = "Manual" })).Expect(HttpStatusCode.Created).Json;
            var updated = await manager.PutAsync($"/api/pos/terminals/{Id(manual)}", new { name = "Manuell", connection = "Simulated", version = manual["version"]!.GetValue<uint>() });
            Assert.Equal((HttpStatusCode.Conflict, "pos.terminal_simulated_not_allowed"), (updated.Status, updated.Code));
            // … and a simulated profile that already exists cannot start (and so never approve) a card transaction.
            var started = await manager.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Payment", amountMinor = 10_00, terminalProfileId = existing });
            Assert.Equal((HttpStatusCode.Conflict, "pos.terminal_simulated_not_allowed"), (started.Status, started.Code));
            // Control: the manual profile works in Production.
            (await manager.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Payment", amountMinor = 10_00, terminalProfileId = Id(manual) }))
                .Expect(HttpStatusCode.Created);
        }

        await using var staging = Production(allow: true);
        var stagingManager = await ManagerAsync(staging);
        (await stagingManager.PostAsync($"/api/pos/registers/{register}/terminals", new { name = "Sim Staging", connection = "Simulated" })).Expect(HttpStatusCode.Created);
        (await stagingManager.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Payment", amountMinor = 10_00, terminalProfileId = existing }))
            .Expect(HttpStatusCode.Created);
    }
}
