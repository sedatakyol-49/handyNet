using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Offline mode (ADR-0022) on real PostgreSQL: the register switch, booking a sale recorded offline (server time, next gapless
/// number, offline record on the immutable receipt), time and payment rules, the device reference unique per register, and
/// the Idempotency-Key replay.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosOfflineTests(HandyNetFixture fixture)
{
    private readonly PosSupport _pos = new(fixture);

    private static Guid Id(JsonNode node, string property = "id") => PosSupport.Id(node, property);

    private static object Sale(Guid register, Guid product, object? offline, string method = "Cash", int quantity = 1) => new
    {
        registerId = register,
        lines = new[] { new { productId = product, quantity, unitPriceMinor = 10_00 } },
        payments = new[] { new { method, amountMinor = 10_00L * quantity } },
        offline,
    };

    private static async Task EnableOfflineAsync(ApiClient shop, Guid register, bool on = true) =>
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, offlineMode = on })).Expect(HttpStatusCode.OK);

    [Fact]
    public async Task Offline_mode_is_off_by_default_and_switched_by_a_manager_only()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        Assert.False((await shop.GetAsync($"/api/pos/registers/{register}")).Expect(HttpStatusCode.OK).Json["offlineMode"]!.GetValue<bool>());

        var staff = await _pos.MemberAsync(shop, "Staff");
        (await staff.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, offlineMode = true })).Expect(HttpStatusCode.Forbidden);

        var updated = (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, offlineMode = true })).Expect(HttpStatusCode.OK).Json;
        Assert.True(updated["offlineMode"]!.GetValue<bool>());
        // Omitting the field leaves it unchanged (older clients).
        var unchanged = (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true })).Expect(HttpStatusCode.OK).Json;
        Assert.True(unchanged["offlineMode"]!.GetValue<bool>());
        Assert.True((await staff.GetAsync("/api/pos/registers")).Expect(HttpStatusCode.OK).Json.AsArray().Single()!["offlineMode"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Offline_sale_is_booked_with_server_time_the_next_number_and_its_offline_record()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 10);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        var recordedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var offline = new { recordedAt, reference = "OFF-AB12-1" };

        // Not enabled on the register: refused, and no number is used.
        var refused = await shop.PostAsync("/api/pos/sales", Sale(register, product, offline), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.offline_not_enabled"), (refused.Status, refused.Code));

        await EnableOfflineAsync(shop, register);
        var first = (await shop.PostAsync("/api/pos/sales", Sale(register, product, null), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(1, first["number"]!.GetValue<long>());
        Assert.Null(first["offline"]);

        var key = PosSupport.Key();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var booked = (await shop.PostAsync("/api/pos/sales", Sale(register, product, offline), key)).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(2, booked["number"]!.GetValue<long>()); // numbers are never allocated offline
        Assert.True(booked["createdAt"]!.GetValue<DateTimeOffset>() >= before); // server time of the booking
        Assert.Equal("OFF-AB12-1", booked["offline"]!["reference"]!.GetValue<string>());
        Assert.True((booked["offline"]!["recordedAt"]!.GetValue<DateTimeOffset>() - recordedAt).Duration() < TimeSpan.FromMilliseconds(1));
        var notes = booked["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains(notes, n => n.StartsWith("Offline erfasst am ") && n.EndsWith(" · Offline-Nr. OFF-AB12-1 · nachträglich gebucht und signiert"));
        Assert.Equal("OFF-AB12-1", booked["document"]!["offline"]!["reference"]!.GetValue<string>());

        // Stored on the immutable receipt.
        Assert.Equal("OFF-AB12-1", await _pos.OwnerScalarAsync<string>($"SELECT offline_reference FROM pos.receipts WHERE id = '{Id(booked)}'"));
        var immutable = await _pos.OwnerTryAsync($"UPDATE pos.receipts SET offline_reference = 'X' WHERE id = '{Id(booked)}'");
        Assert.NotNull(immutable);

        // The sync retries with the original Idempotency-Key: the stored receipt, no second booking.
        var replay = (await shop.PostAsync("/api/pos/sales", Sale(register, product, offline), key)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(booked), Id(replay));
        Assert.Equal("OFF-AB12-1", replay["offline"]!["reference"]!.GetValue<string>());

        // The same device reference under a new key is refused on this register …
        var reused = await shop.PostAsync("/api/pos/sales", Sale(register, product, offline), PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.offline_reference_used"), (reused.Status, reused.Code));
        // … but another register may use it (references are per device/register).
        var other = await PosSupport.RegisterWithSessionAsync(shop, "Kasse 2");
        (await shop.PutAsync($"/api/pos/registers/{other}", new { name = "Kasse 2", isActive = true, offlineMode = true })).Expect(HttpStatusCode.OK);
        (await shop.PostAsync("/api/pos/sales", Sale(other, product, offline), PosSupport.Key())).Expect(HttpStatusCode.Created);

        // The refused attempts gave their numbers back: the next sale is number 3.
        var next = (await shop.PostAsync("/api/pos/sales", Sale(register, product, null), PosSupport.Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(3, next["number"]!.GetValue<long>());

        // History: the summary flags offline receipts; the detail read shows the record.
        var list = (await shop.GetAsync($"/api/pos/sales?registerId={register}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal([false, true, false], list.OrderBy(i => i!["number"]!.GetValue<long>()).Select(i => i!["offline"]!.GetValue<bool>()));
        var read = (await shop.GetAsync($"/api/pos/sales/{Id(booked)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("OFF-AB12-1", read["offline"]!["reference"]!.GetValue<string>());
    }

    [Fact]
    public async Task Offline_sale_is_refused_for_implausible_times_and_bad_references()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 10);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        await EnableOfflineAsync(shop, register);

        async Task<ApiResponse> AttemptAsync(DateTimeOffset at, string reference) =>
            await shop.PostAsync("/api/pos/sales", Sale(register, product, new { recordedAt = at, reference }), PosSupport.Key());

        var tooOld = await AttemptAsync(DateTimeOffset.UtcNow.AddHours(-73), "OFF-1");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_time_invalid"), (tooOld.Status, tooOld.Code));
        var future = await AttemptAsync(DateTimeOffset.UtcNow.AddMinutes(10), "OFF-1");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_time_invalid"), (future.Status, future.Code));
        var badReference = await AttemptAsync(DateTimeOffset.UtcNow, "OFF 1!");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_reference"), (badReference.Status, badReference.Code));
        var tooLong = await AttemptAsync(DateTimeOffset.UtcNow, new string('A', 41));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.offline_reference"), (tooLong.Status, tooLong.Code));

        // Slight clock drift of the device and a record just inside the window are accepted; card is allowed offline.
        (await AttemptAsync(DateTimeOffset.UtcNow.AddMinutes(3), "OFF-2")).Expect(HttpStatusCode.Created);
        (await shop.PostAsync("/api/pos/sales", Sale(register, product, new { recordedAt = DateTimeOffset.UtcNow.AddHours(-71), reference = "OFF-3" }, "Card"),
            PosSupport.Key())).Expect(HttpStatusCode.Created);

        // Nothing was written for the refused attempts: two receipts, numbers 1 and 2.
        Assert.Equal(2L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.receipts WHERE register_id = '{register}'"));
        Assert.Equal(3L, await _pos.OwnerScalarAsync<long>($"SELECT next_receipt_number FROM pos.registers WHERE id = '{register}'"));
    }

    [Fact]
    public async Task Offline_reference_is_unique_per_register_in_the_database_and_under_concurrency()
    {
        var shop = await _pos.ShopAsync();
        var product = await PosSupport.StockProductAsync(shop, 50);
        var register = await PosSupport.RegisterWithSessionAsync(shop);
        await EnableOfflineAsync(shop, register);
        var offline = new { recordedAt = DateTimeOffset.UtcNow.AddMinutes(-10), reference = "OFF-RACE-1" };

        // Eight devices' worth of requests with the same reference but different keys: exactly one is booked.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shop.PostAsync("/api/pos/sales", Sale(register, product, offline), PosSupport.Key())));
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created),
            r => Assert.True(r.Status == HttpStatusCode.Conflict && r.Code == "pos.offline_reference_used", $"{(int)r.Status} {r.Code}"));
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.receipts WHERE register_id = '{register}' AND offline_reference = 'OFF-RACE-1'"));
        Assert.Equal(49, (await shop.GetAsync("/api/inventory/stock?pageSize=200")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Single(i => Id(i!, "productId") == product)!["onHand"]!.GetValue<int>());

        // The database itself enforces it: the unique index and the shape check exist.
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'pos' AND indexname = 'ux_receipts_offline_reference' AND indexdef LIKE 'CREATE UNIQUE INDEX%'"));
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'ck_receipts_offline'"));
    }
}
