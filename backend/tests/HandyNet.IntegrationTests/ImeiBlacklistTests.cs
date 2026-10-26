using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>Platform IMEI blacklist on every device path (Lager entry, POS, sales invoices) and the unit list/history (ADR-0019).</summary>
[Collection(HandyNetCollection.Name)]
public sealed class ImeiBlacklistTests(HandyNetFixture fixture)
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

    private async Task<ApiClient> DealerAsync(params string[] extraModules)
    {
        var shop = await _scenario.RegisterAsync("geraetehandel", wholesaler: false);
        await _scenario.ApproveAsync(shop, ["Inventory", "ProductImport", .. extraModules]);
        return shop;
    }

    /// <summary>A new phone in the catalog with an EAN (as delivered by a supplier).</summary>
    private static async Task<Guid> PhoneAsync(ApiClient shop) =>
        Id((await shop.PostAsync("/api/catalog/products", new
        {
            brand = "TestPhone", name = "Neo 12 128 GB " + Scenario.UniqueSuffix(), category = "Smartphone", quality = "Original", ean = Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json);

    /// <summary>Lager entry ("Artikel anlegen") of devices by IMEI — today's serialized goods receipt.</summary>
    private static Task<ApiResponse> StockAsync(ApiClient shop, Guid productId, string[] imeis, decimal purchasePrice = 450m) =>
        shop.PostAsync("/api/inventory/items", new { productId, imeis, purchasePrice }, Key());

    private static List<(string Imei, string Code)> Problems(ApiResponse response) =>
        response.Json["imeis"]!.AsArray().Select(p => (p!["imei"]!.GetValue<string>(), p["code"]!.GetValue<string>())).ToList();

    private static async Task<int> OnHandAsync(ApiClient shop, Guid productId) =>
        (await shop.GetAsync("/api/inventory/stock?pageSize=200")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
        .SingleOrDefault(i => Id(i!, "productId") == productId)?["onHand"]!.GetValue<int>() ?? 0;

    private static async Task<JsonArray> UnitsAsync(ApiClient shop, string query) =>
        (await shop.GetAsync($"/api/inventory/units?{query}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();

    private async Task<Guid> BlacklistAsync(string imei, string reason = "Stolen")
    {
        var admin = await _scenario.AdminAsync();
        return Id((await admin.PostAsync("/api/admin/imei-blacklist", new { imei, reason, note = "Anzeige", source = "Polizei Berlin" }))
            .Expect(HttpStatusCode.Created).Json);
    }

    private static async Task<Guid> RegisterWithSessionAsync(ApiClient shop)
    {
        var register = Id((await shop.PostAsync("/api/pos/registers", new { name = "Kasse 1" })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 0 })).Expect(HttpStatusCode.Created);
        return register;
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- Lager entry (goods receipt of devices) -------------------------------------------------------------------

    [Fact]
    public async Task Lager_entry_lists_every_blacklisted_or_stocked_imei_and_books_nothing()
    {
        var other = await DealerAsync();
        var taken = NewImei();
        (await StockAsync(other, await PhoneAsync(other), [taken])).Expect(HttpStatusCode.Created);
        var stolen = NewImei();
        await BlacklistAsync(stolen);

        var dealer = await DealerAsync();
        var productId = await PhoneAsync(dealer);
        string[] imeis = [NewImei(), stolen, taken, NewImei()];

        var refused = await StockAsync(dealer, productId, imeis);

        Assert.Equal((HttpStatusCode.Conflict, "entry.imei_conflicts"), (refused.Status, refused.Code));
        Assert.Equal([(stolen, "imei.blacklisted"), (taken, "unit.imei_in_stock")], Problems(refused));
        // Tenants learn only yes/no: neither the reporting source nor the other organization is revealed.
        Assert.DoesNotContain("Polizei", refused.Json.ToJsonString());
        Assert.DoesNotContain("Anzeige", refused.Json.ToJsonString());
        Assert.DoesNotContain(other.OrganizationId!.Value.ToString(), refused.Json.ToJsonString());
        // All or nothing: the two free devices were not booked either.
        Assert.Equal(0, await OnHandAsync(dealer, productId));
        Assert.Equal(0L, await AsOwnerAsync<long>($"SELECT count(*) FROM inventory.stock_units WHERE imei IN ('{imeis[0]}', '{imeis[3]}')"));

        // A single blacklisted IMEI keeps the specific code.
        var single = await StockAsync(dealer, productId, [stolen]);
        Assert.Equal((HttpStatusCode.Conflict, "imei.blacklisted"), (single.Status, single.Code));
        Assert.Equal(stolen, single.Json["imei"]!.GetValue<string>());

        // Without the blocked devices the entry goes through.
        (await StockAsync(dealer, productId, [imeis[0], imeis[3]])).Expect(HttpStatusCode.Created);
        Assert.Equal(2, await OnHandAsync(dealer, productId));
    }

    // ---- unit list and detail --------------------------------------------------------------------------------------

    [Fact]
    public async Task Unit_list_filters_and_detail_shows_the_status_history_after_a_pos_sale()
    {
        var dealer = await DealerAsync("Pos");
        var productId = await PhoneAsync(dealer);
        var sold = NewImei();
        var kept = NewImei();
        (await StockAsync(dealer, productId, [sold, kept])).Expect(HttpStatusCode.Created);

        var register = await RegisterWithSessionAsync(dealer);
        (await dealer.PostAsync("/api/pos/sales", new
        {
            registerId = register, lines = new[] { new { imei = sold, quantity = 1, unitPriceMinor = 599_00 } },
            payments = new[] { new { method = "Card", amountMinor = 599_00 } },
        }, Key())).Expect(HttpStatusCode.Created);

        Assert.Equal([sold], (await UnitsAsync(dealer, $"productId={productId}&status=Sold")).Select(u => u!["imei"]!.GetValue<string>()));
        Assert.Equal([kept], (await UnitsAsync(dealer, $"productId={productId}&status=InStock")).Select(u => u!["imei"]!.GetValue<string>()));
        Assert.Equal([sold], (await UnitsAsync(dealer, $"imei={sold[..12]}")).Select(u => u!["imei"]!.GetValue<string>()));
        Assert.Equal("unit.imei_prefix", (await dealer.GetAsync("/api/inventory/units?imei=35x")).Code);
        var keptUnit = (await UnitsAsync(dealer, $"imei={kept}")).Single()!;
        Assert.Equal(("Standard", 450_00L), (keptUnit["taxScheme"]!.GetValue<string>(), keptUnit["purchasePriceMinor"]!.GetValue<long>()));

        var page = (await dealer.GetAsync($"/api/inventory/units?productId={productId}&pageSize=1")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((2, 1), (page["totalCount"]!.GetValue<int>(), page["items"]!.AsArray().Count));

        var unitId = Id((await UnitsAsync(dealer, $"imei={sold}")).Single()!);
        var detail = (await dealer.GetAsync($"/api/inventory/units/{unitId}")).Expect(HttpStatusCode.OK).Json;
        var history = detail["history"]!.AsArray().Select(h => (h!["type"]!.GetValue<string>(), h["statusAfter"]!.GetValue<string>())).ToList();
        Assert.Equal([("UnitReceipt", "InStock"), ("Sale", "Sold")], history);
        Assert.Equal(HttpStatusCode.NotFound, (await dealer.GetAsync($"/api/inventory/units/{Guid.NewGuid()}")).Status);

        // Another organization sees neither the list entry nor the detail.
        var other = await DealerAsync();
        Assert.Empty(await UnitsAsync(other, $"imei={sold}"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/inventory/units/{unitId}")).Status);
    }

    [Fact]
    public async Task Unit_endpoints_require_the_inventory_module()
    {
        var withoutModule = await _scenario.RegisterAsync("ohnemodul", wholesaler: false);
        await _scenario.ApproveAsync(withoutModule, "Pos");
        Assert.Equal("module.disabled", (await withoutModule.GetAsync("/api/inventory/units")).Code);
    }

    // ---- blacklist on the selling paths ----------------------------------------------------------------------------

    [Fact]
    public async Task A_device_blacklisted_after_stocking_cannot_be_sold_or_returned()
    {
        var dealer = await DealerAsync("Pos");
        var productId = await PhoneAsync(dealer);
        var imei = NewImei();
        var returned = NewImei();
        (await StockAsync(dealer, productId, [imei, returned])).Expect(HttpStatusCode.Created);
        var register = await RegisterWithSessionAsync(dealer);
        object Sale(string i) => new
        {
            registerId = register, lines = new[] { new { imei = i, quantity = 1, unitPriceMinor = 599_00 } },
            payments = new[] { new { method = "Card", amountMinor = 599_00 } },
        };

        // Sold first, reported later: the customer return must not bring it back into stock.
        var sale = (await dealer.PostAsync("/api/pos/sales", Sale(returned), Key())).Expect(HttpStatusCode.Created).Json;
        var entryId = await BlacklistAsync(imei);
        await BlacklistAsync(returned);

        Assert.Equal("imei.blacklisted", (await dealer.GetAsync($"/api/pos/lookup?code={imei}&registerId={register}")).Code);
        var refused = await dealer.PostAsync("/api/pos/sales", Sale(imei), Key());
        Assert.Equal((HttpStatusCode.Conflict, "imei.blacklisted"), (refused.Status, refused.Code));
        Assert.Equal("InStock", await AsOwnerAsync<string>($"SELECT status FROM inventory.stock_units WHERE imei = '{imei}'"));
        Assert.Equal("imei.blacklisted", (await dealer.PostAsync($"/api/pos/sales/{Id(sale)}/returns",
            new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Card", reason = "Widerruf" }, Key())).Code);
        Assert.Equal("Sold", await AsOwnerAsync<string>($"SELECT status FROM inventory.stock_units WHERE imei = '{returned}'"));

        // Deactivated (device found): selling works again.
        var admin = await _scenario.AdminAsync();
        (await admin.PostAsync($"/api/admin/imei-blacklist/{entryId}/deactivate", new { reason = "Gerät wiedergefunden, Anzeige zurückgezogen" }))
            .Expect(HttpStatusCode.OK);
        (await dealer.PostAsync("/api/pos/sales", Sale(imei), Key())).Expect(HttpStatusCode.Created);
    }

    // ---- administration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_maintains_the_blacklist_with_history_audit_and_tenants_cannot_use_the_admin_api()
    {
        var admin = await _scenario.AdminAsync();
        var imei = NewImei();

        var first = (await admin.PostAsync("/api/admin/imei-blacklist", new { imei, reason = "Lost", source = "Kundenmeldung" })).Expect(HttpStatusCode.Created).Json;
        Assert.True(first["isActive"]!.GetValue<bool>());
        Assert.Equal("blacklist.already_active", (await admin.PostAsync("/api/admin/imei-blacklist", new { imei, reason = "Stolen", source = "x" })).Code);
        var invalid = await admin.PostAsync("/api/admin/imei-blacklist", new { imei = "123", reason = "Lost", source = "x" });
        Assert.Equal("validation.failed", invalid.Code);
        Assert.Contains("validation.imei", invalid.Json.ToJsonString());
        Assert.Equal("blacklist.note_required", (await admin.PostAsync("/api/admin/imei-blacklist", new { imei = NewImei(), reason = "Other", source = "x" })).Code);

        var deactivated = (await admin.PostAsync($"/api/admin/imei-blacklist/{Id(first)}/deactivate", new { reason = "Gefunden" })).Expect(HttpStatusCode.OK).Json;
        Assert.False(deactivated["isActive"]!.GetValue<bool>());
        Assert.Equal("blacklist.already_inactive", (await admin.PostAsync($"/api/admin/imei-blacklist/{Id(first)}/deactivate", new { reason = "x" })).Code);

        // Blocking again creates a new entry; the old one stays as history.
        (await admin.PostAsync("/api/admin/imei-blacklist", new { imei, reason = "Fraud", note = "Doppelverkauf", source = "Händler" })).Expect(HttpStatusCode.Created);
        var entries = (await admin.GetAsync($"/api/admin/imei-blacklist?imei={imei}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal([("Fraud", true), ("Lost", false)], entries.Select(e => (e!["reason"]!.GetValue<string>(), e["isActive"]!.GetValue<bool>())));
        Assert.Single((await admin.GetAsync($"/api/admin/imei-blacklist?imei={imei}&active=true")).Json["items"]!.AsArray());
        Assert.Equal("Gefunden", (await admin.GetAsync($"/api/admin/imei-blacklist?imei={imei}&active=false")).Json["items"]!.AsArray()
            .Single()!["deactivationReason"]!.GetValue<string>());
        Assert.Equal("blacklist.search", (await admin.GetAsync("/api/admin/imei-blacklist?imei=35x")).Code);

        // Every block and lift is in the audit log with the admin's reason.
        Assert.Equal(1L, await AsOwnerAsync<long>(
            $"SELECT count(*) FROM infra.audit_log WHERE entity_id = '{Id(first)}' AND action = 'AdminImeiUnblock' AND changes->>'Reason' = 'Gefunden'"));
        Assert.Equal(1L, await AsOwnerAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE entity_id = '{Id(first)}' AND action = 'AdminImeiBlock'"));

        // Tenant users (even owners) are not platform admins.
        var dealer = await DealerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await dealer.GetAsync("/api/admin/imei-blacklist")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await dealer.PostAsync("/api/admin/imei-blacklist", new { imei = NewImei(), reason = "Lost", source = "x" })).Status);
    }

    // ---- database guarantees ---------------------------------------------------------------------------------------

    private async Task<NpgsqlConnection> ConnectAsAppAsync(Guid? organizationId, bool platformAdmin = false)
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false), set_config('app.platform_admin', @admin, false)", connection);
        set.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        set.Parameters.AddWithValue("admin", platformAdmin ? "on" : "off");
        await set.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Tenants_cannot_read_or_write_the_blacklist_and_get_only_yes_or_no()
    {
        var imei = NewImei();
        await BlacklistAsync(imei);
        var dealer = await DealerAsync();

        await using (var asTenant = await ConnectAsAppAsync(dealer.OrganizationId))
        {
            Assert.Equal(0L, await ScalarAsync(asTenant, "SELECT count(*) FROM inventory.imei_blacklist"));
            Assert.Equal(true, await ScalarAsync(asTenant, $"SELECT inventory.imei_blacklisted('{imei}')"));
            Assert.Equal(false, await ScalarAsync(asTenant, $"SELECT inventory.imei_blacklisted('{NewImei()}')"));
            Assert.Equal(false, await ScalarAsync(asTenant, $"SELECT inventory.imei_blacklisted('{imei}0')")); // longer input is not truncated
            Assert.Equal(imei, await ScalarAsync(asTenant, $"SELECT * FROM inventory.imeis_blacklisted(ARRAY['{imei}', '{NewImei()}'])"));

            var insert = Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(asTenant,
                $"INSERT INTO inventory.imei_blacklist (id, imei, reason, source, created_by_user_id, created_at) VALUES (gen_random_uuid(), '{NewImei()}', 'Lost', 'x', gen_random_uuid(), now())"));
            Assert.Equal("42501", (await insert).SqlState); // row-level security
            await using var update = new NpgsqlCommand("UPDATE inventory.imei_blacklist SET note = 'x'", asTenant);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await using (var noTenant = await ConnectAsAppAsync(null))
            Assert.Equal(0L, await ScalarAsync(noTenant, "SELECT count(*) FROM inventory.imei_blacklist"));

        await using (var asAdmin = await ConnectAsAppAsync(null, platformAdmin: true))
        {
            Assert.Equal(1L, await ScalarAsync(asAdmin, $"SELECT count(*) FROM inventory.imei_blacklist WHERE imei = '{imei}'"));
            foreach (var sql in new[]
                     {
                         $"DELETE FROM inventory.imei_blacklist WHERE imei = '{imei}'",
                         "TRUNCATE inventory.imei_blacklist",
                         $"UPDATE inventory.imei_blacklist SET reason = 'Other' WHERE imei = '{imei}'",
                         $"UPDATE inventory.imei_blacklist SET deactivated_at = now(), deactivated_by_user_id = gen_random_uuid(), deactivation_reason = 'x', note = 'changed' WHERE imei = '{imei}'",
                     })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(asAdmin, sql));
                Assert.True(ex.SqlState == "42501", $"{sql}: {ex.SqlState} {ex.MessageText}");
            }
        }
    }

    [Fact]
    public async Task The_database_refuses_a_blacklisted_unit_even_when_the_application_is_bypassed()
    {
        var dealer = await DealerAsync();
        var productId = await PhoneAsync(dealer);
        var stocked = NewImei();
        (await StockAsync(dealer, productId, [stocked])).Expect(HttpStatusCode.Created);
        var warehouseId = Id((await dealer.GetAsync("/api/inventory/warehouses")).Json.AsArray().First(w => w!["isDefault"]!.GetValue<bool>())!);
        var stolen = NewImei();
        await BlacklistAsync(stolen);

        await using var asTenant = await ConnectAsAppAsync(dealer.OrganizationId);
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(asTenant, $"""
            INSERT INTO inventory.stock_units (id, organization_id, warehouse_id, product_id, imei, status, tax_scheme, purchase_price_minor, currency, source, received_at)
            VALUES (gen_random_uuid(), '{dealer.OrganizationId}', '{warehouseId}', '{productId}', '{stolen}', 'InStock', 'Standard', 0, 'EUR', 'sql', now())
            """));
        Assert.Equal("HNB01", insert.SqlState);

        // Reported after stocking: selling by status change is refused as well.
        await BlacklistAsync(stocked);
        var sell = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(asTenant,
            $"UPDATE inventory.stock_units SET status = 'Sold', closed_at = now() WHERE imei = '{stocked}'"));
        Assert.Equal("HNB01", sell.SqlState);
    }
}
