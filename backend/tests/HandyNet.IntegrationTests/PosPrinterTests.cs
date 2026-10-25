using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Receipt printer profiles per register (ADR-0018) on real PostgreSQL with RLS: CRUD, one default per register, connection
/// rules, permissions (read with pos.sell, write with pos.manage), optimistic concurrency and tenant isolation.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosPrinterTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);
    private const string StaffPassword = "Kassierer-Passwort-1";

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    private async Task<(ApiClient Shop, Guid RegisterId)> ShopWithRegisterAsync()
    {
        var shop = await _scenario.RegisterAsync("druckershop", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Pos", "Inventory");
        var register = (await shop.PostAsync("/api/pos/registers", new { name = "Kasse 1" })).Expect(HttpStatusCode.Created).Json;
        return (shop, Id(register));
    }

    private async Task<ApiClient> MemberAsync(ApiClient owner, string role)
    {
        var email = $"drucker-{Scenario.UniqueSuffix()}@test.de";
        (await owner.PostAsync("/api/organization/staff", new { email, displayName = role, password = StaffPassword, role })).Expect(HttpStatusCode.OK);
        var client = fixture.CreateClient();
        await client.LoginAsync(email, StaffPassword);
        client.OrganizationId = owner.OrganizationId;
        return client;
    }

    private static object Epson(string name = "Theke Epson", string host = "192.168.1.50") => new
    {
        name, connection = "EpsonEpos", paperWidthMm = 80, charsPerLine = 48, autoPrint = true, openDrawerOnCash = true, copies = 1,
        cutPaper = true, host, useHttps = true,
    };

    private static Task<ApiResponse> DeleteAsync(ApiClient client, string path) => client.SendAsync(HttpMethod.Delete, path);

    [Fact]
    public async Task Profiles_are_created_listed_updated_made_default_and_deleted()
    {
        var (shop, registerId) = await ShopWithRegisterAsync();
        var url = $"/api/pos/registers/{registerId}/printers";
        Assert.Empty((await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray());

        // The first profile becomes the default; fields of other connection types are dropped; the ePOS device id defaults.
        var epson = (await shop.PostAsync(url, new
        {
            name = "Theke Epson", connection = "EpsonEpos", paperWidthMm = 80, charsPerLine = 48, autoPrint = true, openDrawerOnCash = true, copies = 2,
            cutPaper = true, host = "192.168.1.50", useHttps = true, bridgeUrl = "http://localhost:9123/print", usbVendorId = 1208, baudRate = 9600,
        })).Expect(HttpStatusCode.Created).Json;
        Assert.True(epson["isDefault"]!.GetValue<bool>());
        Assert.Equal("local_printer", epson["eposDeviceId"]!.GetValue<string>());
        Assert.Null(epson["bridgeUrl"]);
        Assert.Null(epson["usbVendorId"]);
        Assert.Null(epson["baudRate"]);
        Assert.Equal("Cp858", epson["codepage"]!.GetValue<string>());
        Assert.True(epson["printQr"]!.GetValue<bool>());

        // A bridge profile gets the raw port 9100 by default; a USB profile remembers the device ids.
        var bridge = (await shop.PostAsync(url, new
        {
            name = "Bridge 9100", connection = "EscPosBridge", paperWidthMm = 58, charsPerLine = 32, autoPrint = false, openDrawerOnCash = false, copies = 1,
            cutPaper = false, host = "10.0.0.20", bridgeUrl = "http://localhost:9123/print", codepage = "Pc437",
        })).Expect(HttpStatusCode.Created).Json;
        Assert.False(bridge["isDefault"]!.GetValue<bool>());
        Assert.Equal(9100, bridge["port"]!.GetValue<int>());
        Assert.False(bridge["useHttps"]!.GetValue<bool>());
        var usb = (await shop.PostAsync(url, new
        {
            name = "USB", connection = "WebUsb", paperWidthMm = 80, charsPerLine = 42, autoPrint = true, openDrawerOnCash = false, copies = 1,
            cutPaper = true, usbVendorId = 0x04B8, usbProductId = 0x0202, host = "ignored.example",
        })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(0x04B8, usb["usbVendorId"]!.GetValue<int>());
        Assert.Null(usb["host"]);

        var list = (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(["Theke Epson", "Bridge 9100", "USB"], list.Select(p => p!["name"]!.GetValue<string>()));

        // Make the bridge the default: exactly one default afterwards.
        (await shop.PostAsync($"/api/pos/printers/{Id(bridge)}/default")).Expect(HttpStatusCode.OK);
        list = (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal("Bridge 9100", list.Single(p => p!["isDefault"]!.GetValue<bool>())!["name"]!.GetValue<string>());

        // Update with the current version; a stale version is a 409.
        var current = (await shop.GetAsync($"/api/pos/printers/{Id(epson)}")).Expect(HttpStatusCode.OK).Json;
        var version = current["version"]!.GetValue<uint>();
        var updated = (await shop.PutAsync($"/api/pos/printers/{Id(epson)}", new
        {
            name = "Theke Epson 58", connection = "EpsonEpos", paperWidthMm = 58, charsPerLine = 32, autoPrint = false, openDrawerOnCash = true, copies = 1,
            cutPaper = true, host = "printer.local", port = 8443, useHttps = true, eposDeviceId = "local_printer", version,
        })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("printer.local", updated["host"]!.GetValue<string>());
        Assert.Equal(8443, updated["port"]!.GetValue<int>());
        Assert.Equal(58, updated["paperWidthMm"]!.GetValue<int>());
        var stale = await shop.PutAsync($"/api/pos/printers/{Id(epson)}", new
        {
            name = "Alt", connection = "Browser", paperWidthMm = 80, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 1,
            cutPaper = false, version,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("concurrency", stale.Code);
        // Without a version the update is refused.
        Assert.Equal("pos.printer_version", (await shop.PutAsync($"/api/pos/printers/{Id(epson)}", Epson())).Code);

        // Names are unique per register.
        var duplicate = await shop.PostAsync(url, Epson("USB"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Equal("pos.printer_name_taken", duplicate.Code);

        // Deleting the default promotes the remaining profile with the first name.
        (await DeleteAsync(shop, $"/api/pos/printers/{Id(bridge)}")).Expect(HttpStatusCode.NoContent);
        list = (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("Theke Epson 58", list.Single(p => p!["isDefault"]!.GetValue<bool>())!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await shop.GetAsync($"/api/pos/printers/{Id(bridge)}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(shop, $"/api/pos/printers/{Id(bridge)}")).Status);
    }

    [Theory]
    [InlineData("EpsonEpos", null, null, "pos.printer_host")]
    [InlineData("EpsonEpos", "http://192.168.1.50/cgi-bin", null, "pos.printer_host")]
    [InlineData("StarWebPrnt", "192.168.1.50:80", null, "pos.printer_host")]
    [InlineData("EscPosBridge", "192.168.1.50", null, "pos.printer_bridge_url")]
    [InlineData("EscPosBridge", "192.168.1.50", "ftp://localhost/print", "pos.printer_bridge_url")]
    [InlineData("EscPosBridge", "192.168.1.50", "http://user:secret@localhost:9123/print", "pos.printer_bridge_url")]
    public async Task Connection_settings_are_validated(string connection, string? host, string? bridgeUrl, string code)
    {
        var (shop, registerId) = await ShopWithRegisterAsync();
        var response = await shop.PostAsync($"/api/pos/registers/{registerId}/printers", new
        {
            name = "X", connection, paperWidthMm = 80, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 1, cutPaper = true,
            host, bridgeUrl,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
        Assert.Equal(code, response.Code);
    }

    [Fact]
    public async Task Field_ranges_are_validated_and_ipv6_hosts_accepted()
    {
        var (shop, registerId) = await ShopWithRegisterAsync();
        var url = $"/api/pos/registers/{registerId}/printers";
        foreach (var body in new object[]
                 {
                     new { name = "X", connection = "Browser", paperWidthMm = 76, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 1, cutPaper = true },
                     new { name = "X", connection = "Browser", paperWidthMm = 80, charsPerLine = 100, autoPrint = false, openDrawerOnCash = false, copies = 1, cutPaper = true },
                     new { name = "X", connection = "Browser", paperWidthMm = 80, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 9, cutPaper = true },
                     new { name = "", connection = "Browser", paperWidthMm = 80, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 1, cutPaper = true },
                 })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync(url, body)).Status);

        var serial = await shop.PostAsync(url, new
        {
            name = "Seriell", connection = "WebSerial", paperWidthMm = 80, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 1,
            cutPaper = true, baudRate = 12345,
        });
        Assert.Equal("pos.printer_baud_rate", serial.Code);

        var star = (await shop.PostAsync(url, new
        {
            name = "Star", connection = "StarWebPrnt", paperWidthMm = 80, charsPerLine = 48, autoPrint = false, openDrawerOnCash = false, copies = 1,
            cutPaper = true, host = "fe80::1",
        })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("fe80::1", star["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task Staff_can_read_profiles_but_only_managers_write()
    {
        var (shop, registerId) = await ShopWithRegisterAsync();
        var profile = (await shop.PostAsync($"/api/pos/registers/{registerId}/printers", Epson())).Expect(HttpStatusCode.Created).Json;
        var staff = await MemberAsync(shop, "Staff");
        var manager = await MemberAsync(shop, "Manager");

        Assert.Single((await staff.GetAsync($"/api/pos/registers/{registerId}/printers")).Expect(HttpStatusCode.OK).Json.AsArray());
        (await staff.GetAsync($"/api/pos/printers/{Id(profile)}")).Expect(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsync($"/api/pos/registers/{registerId}/printers", Epson("Staff"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsync($"/api/pos/printers/{Id(profile)}/default")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(staff, $"/api/pos/printers/{Id(profile)}")).Status);

        (await manager.PostAsync($"/api/pos/registers/{registerId}/printers", Epson("Manager"))).Expect(HttpStatusCode.Created);
        (await DeleteAsync(manager, $"/api/pos/printers/{Id(profile)}")).Expect(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Other_tenants_cannot_see_or_change_profiles()
    {
        var (shop, registerId) = await ShopWithRegisterAsync();
        var profile = (await shop.PostAsync($"/api/pos/registers/{registerId}/printers", Epson())).Expect(HttpStatusCode.Created).Json;
        var (other, _) = await ShopWithRegisterAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/pos/registers/{registerId}/printers")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/pos/printers/{Id(profile)}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/pos/registers/{registerId}/printers", Epson("Fremd"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/pos/printers/{Id(profile)}/default")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(other, $"/api/pos/printers/{Id(profile)}")).Status);

        // The database enforces it without the application's WHERE clause.
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection))
        {
            set.Parameters.AddWithValue("org", other.OrganizationId!.Value.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM pos.printer_profiles WHERE id = '{Id(profile)}'", connection))
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        await using (var delete = new NpgsqlCommand($"DELETE FROM pos.printer_profiles WHERE id = '{Id(profile)}'", connection))
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        await using (var insert = new NpgsqlCommand(
                         $"INSERT INTO pos.printer_profiles (id, organization_id, register_id, name, connection, paper_width_mm, chars_per_line, auto_print, "
                         + "open_drawer_on_cash, copies, cut_paper, print_qr, codepage, use_https, is_default, created_at, updated_at) "
                         + $"VALUES (gen_random_uuid(), '{shop.OrganizationId}', '{registerId}', 'X', 'Browser', 80, 48, false, false, 1, false, false, 'Cp858', false, false, now(), now())",
                         connection))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }
        (await shop.GetAsync($"/api/pos/printers/{Id(profile)}")).Expect(HttpStatusCode.OK);
    }
}
