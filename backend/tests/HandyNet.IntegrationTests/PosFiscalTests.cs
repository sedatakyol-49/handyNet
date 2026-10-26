using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Pos.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>Fiscal layer (ADR-0012) on real PostgreSQL: simulated TSE, outages, admin configuration, RLS, immutability, DSFinV-K.</summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosFiscalTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private static Dictionary<string, string> Key(string? key = null) => new() { ["Idempotency-Key"] = key ?? Guid.NewGuid().ToString("N") };

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    private async Task<(ApiClient Shop, Guid Register, Guid Product)> ShopAsync(int stock = 20)
    {
        var shop = await _scenario.RegisterAsync("tse-shop", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Pos", "Inventory", "ProductImport");
        var product = Id((await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = "Panzerglas " + Scenario.UniqueSuffix(), category = "Accessory", quality = "Aftermarket", ean = Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json);
        var warehouses = (await shop.GetAsync("/api/inventory/warehouses")).Expect(HttpStatusCode.OK).Json.AsArray();
        var warehouseId = warehouses.Count > 0
            ? Id(warehouses.First(w => w!["isDefault"]!.GetValue<bool>())!)
            : Id((await shop.PostAsync("/api/inventory/warehouses", new { code = "MAIN", name = "Hauptlager" })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId = product, delta = stock, reason = "Wareneingang", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        var register = Id((await shop.PostAsync("/api/pos/registers", new { name = "Kasse TSE" })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 100_00 })).Expect(HttpStatusCode.Created);
        return (shop, register, product);
    }

    private static object Sale(Guid register, Guid product, long price = 19_00, string method = "Cash") => new
    {
        registerId = register,
        lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = price } },
        payments = new[] { new { method, amountMinor = price } },
    };

    private async Task<JsonNode> ConfigureAsync(ApiClient shop, object body, Guid? register = null)
    {
        var admin = await _scenario.AdminAsync();
        var path = register is null ? $"/api/admin/pos/fiscal/{shop.OrganizationId}" : $"/api/admin/pos/fiscal/{shop.OrganizationId}/registers/{register}";
        return (await admin.PutAsync(path, body)).Expect(HttpStatusCode.OK).Json;
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<PostgresException> OwnerFailsAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private static TseLogMessage FinishMessage(JsonNode fiscal) => new(TseLogMessage.FinishOperation, fiscal["tseSerialNumber"]!.GetValue<string>(),
        fiscal["clientId"]!.GetValue<string>(), fiscal["transactionNumber"]!.GetValue<long>(), fiscal["signatureCounter"]!.GetValue<long>(),
        DateTimeOffset.Parse(fiscal["finishedAt"]!.GetValue<string>()), fiscal["processType"]!.GetValue<string>(), fiscal["processData"]!.GetValue<string>());

    [Fact]
    public async Task Simulated_tse_signs_sales_storno_cash_movements_and_closing()
    {
        var (shop, register, product) = await ShopAsync();
        var config = await ConfigureAsync(shop, new { mode = "SimulatedTse", algorithm = "ecdsa-plain-SHA384" });
        Assert.Equal("SimulatedTse", config["mode"]!.GetValue<string>());
        var tse = config["simulatedTse"]!;
        var serial = tse["serialNumber"]!.GetValue<string>();
        Assert.Equal(0, tse["transactionCounter"]!.GetValue<long>());

        var status = (await shop.GetAsync($"/api/pos/registers/{register}/fiscal")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("SimulatedTse", "Test", true, serial), (status["mode"]!.GetValue<string>(), status["state"]!.GetValue<string>(),
            status["isTest"]!.GetValue<bool>(), status["tseSerialNumber"]!.GetValue<string>()));

        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created).Json;
        var fiscal = sale["fiscal"]!;
        Assert.Equal("Signed", fiscal["status"]!.GetValue<string>());
        Assert.True(fiscal["isTest"]!.GetValue<bool>());
        Assert.Equal(serial, fiscal["tseSerialNumber"]!.GetValue<string>());
        Assert.Equal((1L, 2L), (fiscal["transactionNumber"]!.GetValue<long>(), fiscal["signatureCounter"]!.GetValue<long>()));
        Assert.Equal("Kassenbeleg-V1", fiscal["processType"]!.GetValue<string>());
        Assert.Equal("Beleg^19.00_0.00_0.00_0.00_0.00^19.00:Bar", fiscal["processData"]!.GetValue<string>());
        Assert.Equal(register.ToString("N"), fiscal["clientId"]!.GetValue<string>());
        Assert.True(SimulatedTseCrypto.Verify(fiscal["publicKey"]!.GetValue<string>(), "ecdsa-plain-SHA384", FinishMessage(fiscal),
            fiscal["signature"]!.GetValue<string>()));
        var qr = fiscal["qrPayload"]!.GetValue<string>().Split(';');
        Assert.Equal(12, qr.Length);
        Assert.Equal(("V0", "1", "2"), (qr[0], qr[4], qr[5]));
        var document = sale["document"]!;
        Assert.Contains(document["notes"]!.AsArray(), n => n!.GetValue<string>() == KassenSichV.TestTseNotice);
        Assert.Equal(fiscal["qrPayload"]!.GetValue<string>(), document["fiscal"]!["qrPayload"]!.GetValue<string>());

        // A retried request (same Idempotency-Key) returns the stored receipt and never signs again.
        var key = Guid.NewGuid().ToString("N");
        var second = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 5_00, "Card"), Key(key))).Expect(HttpStatusCode.Created).Json;
        var replay = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 5_00, "Card"), Key(key))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(second["fiscal"]!["signature"]!.GetValue<string>(), replay["fiscal"]!["signature"]!.GetValue<string>());
        Assert.Equal(2L, await AsOwnerAsync<long>($"SELECT transaction_counter FROM pos.simulated_tses WHERE serial_number = '{serial}'"));
        Assert.Equal("Beleg^5.00_0.00_0.00_0.00_0.00^5.00:Unbar", second["fiscal"]!["processData"]!.GetValue<string>());

        // Storno: own TSE transaction with a negative process data.
        var storno = (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((3L, 6L), (storno["fiscal"]!["transactionNumber"]!.GetValue<long>(), storno["fiscal"]!["signatureCounter"]!.GetValue<long>()));
        Assert.Equal("Beleg^-19.00_0.00_0.00_0.00_0.00^-19.00:Bar", storno["fiscal"]!["processData"]!.GetValue<string>());

        // Cash movement and closing: signed as "SonstigerVorgang".
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Expect(HttpStatusCode.OK).Json;
        var movement = (await shop.PostAsync($"/api/pos/sessions/{session["id"]}/cash-movements", new { kind = "Deposit", amountMinor = 50_00, reasonCode = "ChangeFund" }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Einlage^ChangeFund^50.00", await AsOwnerAsync<string>(
            $"SELECT data->>'processData' FROM pos.fiscal_records WHERE kind = 'CashMovement' AND reference_id = '{movement["id"]}'"));
        (await shop.PostAsync($"/api/pos/sessions/{session["id"]}/close", new { countedCashMinor = 150_00 })).Expect(HttpStatusCode.OK);
        Assert.Equal("Signed", await AsOwnerAsync<string>($"SELECT status FROM pos.fiscal_records WHERE kind = 'SessionClosing' AND reference_id = '{session["id"]}'"));
        Assert.Equal("SonstigerVorgang", await AsOwnerAsync<string>(
            $"SELECT data->>'processType' FROM pos.fiscal_records WHERE kind = 'SessionClosing' AND reference_id = '{session["id"]}'"));
        Assert.Equal(5L, await AsOwnerAsync<long>($"SELECT transaction_counter FROM pos.simulated_tses WHERE serial_number = '{serial}'"));
        Assert.Equal(10L, await AsOwnerAsync<long>($"SELECT signature_counter FROM pos.simulated_tses WHERE serial_number = '{serial}'"));

        // TSE fields on receipts are immutable (existing GoBD trigger), TSE state cannot be rewound, finished transactions stay finished.
        Assert.Equal("42501", (await OwnerFailsAsync($"UPDATE pos.receipts SET fiscal_signature = 'x' WHERE id = '{sale["id"]}'")).SqlState);
        Assert.Equal("42501", (await OwnerFailsAsync($"UPDATE pos.receipts SET fiscal_qr_payload = NULL WHERE id = '{sale["id"]}'")).SqlState);
        Assert.Equal("23514", (await OwnerFailsAsync($"UPDATE pos.simulated_tses SET signature_counter = 1 WHERE serial_number = '{serial}'")).SqlState);
        Assert.Equal("42501", (await OwnerFailsAsync($"UPDATE pos.simulated_tses SET public_key = 'x' WHERE serial_number = '{serial}'")).SqlState);
        Assert.Equal("42501", (await OwnerFailsAsync($"UPDATE pos.simulated_tse_transactions SET signature = 'x' WHERE transaction_key = '{sale["id"]}'")).SqlState);
        Assert.Equal("42501", (await OwnerFailsAsync($"DELETE FROM pos.simulated_tse_transactions WHERE transaction_key = '{sale["id"]}'")).SqlState);
        Assert.Equal("42501", (await OwnerFailsAsync($"UPDATE pos.fiscal_records SET status = 'Failed' WHERE reference_id = '{movement["id"]}'")).SqlState);
    }

    [Fact]
    public async Task Outage_completes_the_sale_with_the_marker_records_the_outage_and_ends_it_on_recovery()
    {
        var (shop, register, product) = await ShopAsync();
        await ConfigureAsync(shop, new { mode = "SimulatedTse", forcedOutage = true });
        Assert.Equal("Outage", (await shop.GetAsync($"/api/pos/registers/{register}/fiscal")).Expect(HttpStatusCode.OK).Json["state"]!.GetValue<string>());

        var key = Guid.NewGuid().ToString("N");
        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key(key))).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Failed", sale["fiscal"]!["status"]!.GetValue<string>());
        Assert.Equal(KassenSichV.OutageNotice, sale["fiscal"]!["notice"]!.GetValue<string>());
        Assert.Null(sale["fiscal"]!["signature"]);
        Assert.Contains(sale["document"]!["notes"]!.AsArray(), n => n!.GetValue<string>() == KassenSichV.OutageNotice);
        // The retry of the same request returns the same receipt.
        Assert.Equal(Id(sale), Id((await shop.PostAsync("/api/pos/sales", Sale(register, product), Key(key))).Expect(HttpStatusCode.OK).Json));

        // A second failed sale keeps the one open outage.
        (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created);
        var detail = await ConfigureAsync(shop, new { mode = "SimulatedTse", forcedOutage = true });
        var outage = detail["outages"]!.AsArray().Single()!;
        Assert.Null(outage["endedAt"]);
        Assert.Equal(register, Id(outage, "registerId"));
        Assert.NotNull(outage["sessionId"]);
        var status = (await shop.GetAsync($"/api/pos/registers/{register}/fiscal")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Outage", status["state"]!.GetValue<string>());
        Assert.NotNull(status["outageSince"]);

        // TSE back: the next sale is signed and closes the outage; an ended outage can never be reopened.
        await ConfigureAsync(shop, new { mode = "SimulatedTse", forcedOutage = false });
        var signed = (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Signed", signed["fiscal"]!["status"]!.GetValue<string>());
        Assert.Equal(1L, signed["fiscal"]!["transactionNumber"]!.GetValue<long>()); // the failed sales never reached the TSE
        Assert.True(await AsOwnerAsync<bool>($"SELECT ended_at IS NOT NULL FROM pos.fiscal_outages WHERE register_id = '{register}'"));
        Assert.Equal("Test", (await shop.GetAsync($"/api/pos/registers/{register}/fiscal")).Json["state"]!.GetValue<string>());
        Assert.Equal("42501", (await OwnerFailsAsync($"UPDATE pos.fiscal_outages SET ended_at = NULL WHERE register_id = '{register}'")).SqlState);
        Assert.Equal("42501", (await OwnerFailsAsync($"DELETE FROM pos.fiscal_outages WHERE register_id = '{register}'")).SqlState);
    }

    [Fact]
    public async Task Persistent_transient_failures_end_in_the_outage_path_and_register_overrides_apply()
    {
        var (shop, register, product) = await ShopAsync();
        var other = Id((await shop.PostAsync("/api/pos/registers", new { name = "Kasse 2" })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync($"/api/pos/registers/{other}/sessions", new { openingCashMinor = 0 })).Expect(HttpStatusCode.Created);
        await ConfigureAsync(shop, new { mode = "SimulatedTse" });
        var detail = await ConfigureAsync(shop, new { failureRatePercent = 100, clientId = "KASSE-ZWEI" }, other);
        var overridden = detail["registers"]!.AsArray().Single(r => Id(r!, "registerId") == other)!;
        Assert.Equal(("KASSE-ZWEI", 100, "Organization"), (overridden["clientId"]!.GetValue<string>(), overridden["failureRatePercent"]!.GetValue<int>(),
            overridden["modeSource"]!.GetValue<string>()));

        // Register 1 signs normally; register 2 fails every attempt (start and retries) → outage marker, sale completed.
        Assert.Equal("Signed", (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created).Json["fiscal"]!["status"]!
            .GetValue<string>());
        var failed = (await shop.PostAsync("/api/pos/sales", Sale(other, product), Key())).Expect(HttpStatusCode.Created).Json["fiscal"]!;
        Assert.Equal("Failed", failed["status"]!.GetValue<string>());
        Assert.Contains("simulated failure", failed["error"]!.GetValue<string>());
        Assert.Equal("KASSE-ZWEI", failed["clientId"]!.GetValue<string>());

        // Per-register mode override: register 2 without TSE (Disabled).
        await ConfigureAsync(shop, new { mode = "Disabled" }, other);
        Assert.Equal("NotConnected", (await shop.PostAsync("/api/pos/sales", Sale(other, product), Key())).Expect(HttpStatusCode.Created).Json["fiscal"]!["status"]!
            .GetValue<string>());
        Assert.Equal("Disabled", (await shop.GetAsync($"/api/pos/registers/{other}/fiscal")).Json["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Admin_configuration_is_admin_only_validated_and_never_returns_secrets()
    {
        var (shop, register, product) = await ShopAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.PutAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}", new { mode = "SimulatedTse" })).Status);

        var admin = await _scenario.AdminAsync();
        Assert.Equal("validation.failed", (await admin.PutAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}", new { mode = "SimulatedTse", failureRatePercent = 101 })).Code);
        Assert.Equal("pos.fiscal_mode_required", (await admin.PutAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}", new { latencyMs = 10 })).Code);
        Assert.Equal("pos.register_not_found",
            (await admin.PutAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}/registers/{Guid.NewGuid()}", new { mode = "Disabled" })).Code);
        Assert.Equal("organization.not_found", (await admin.GetAsync($"/api/admin/pos/fiscal/{Guid.NewGuid()}")).Code);

        // fiskaly: credentials are stored encrypted (Data Protection) and only reported as "stored".
        var saved = (await admin.PutAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}", new
        {
            mode = "FiskalyCloudTse", fiskalyTssId = Guid.NewGuid(), fiskalyEnvironment = "Sandbox", fiskalyApiKey = "test_key_123", fiskalyApiSecret = "geheim-456",
        })).Expect(HttpStatusCode.OK).Json;
        Assert.True(saved["configuration"]!["hasStoredFiskalyCredentials"]!.GetValue<bool>());
        Assert.DoesNotContain("geheim-456", saved.ToJsonString());
        Assert.DoesNotContain("test_key_123", saved.ToJsonString());
        var stored = await AsOwnerAsync<string>($"SELECT fiskaly_api_secret_protected FROM pos.fiscal_configurations WHERE organization_id = '{shop.OrganizationId}'");
        Assert.DoesNotContain("geheim-456", stored);
        Assert.Equal(0L, await AsOwnerAsync<long>(
            $"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'FiscalConfiguration' AND (changes::text LIKE '%geheim-456%' OR changes::text LIKE '%test_key_123%')"));

        // A credential reference that does not exist in configuration: sales are refused with a stable code, no TSE call.
        await ConfigureAsync(shop, new { mode = "FiskalyCloudTse", fiskalyTssId = Guid.NewGuid(), fiskalyCredentialReference = "missing", clearFiskalyCredentials = true });
        Assert.Equal("pos.fiscal_configuration_incomplete", (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Code);

        // The admin overview lists organizations with the POS module and their mode.
        var list = (await admin.GetAsync($"/api/admin/pos/fiscal?search={Uri.EscapeDataString("tse-shop")}")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Contains(list, o => Id(o!, "organizationId") == shop.OrganizationId && o!["mode"]!.GetValue<string>() == "FiskalyCloudTse");
    }

    [Fact]
    public async Task Fiscal_tables_are_tenant_isolated_and_append_only_for_the_app_role()
    {
        var (shop, register, product) = await ShopAsync();
        await ConfigureAsync(shop, new { mode = "SimulatedTse", forcedOutage = true });
        (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created);
        await ConfigureAsync(shop, new { mode = "SimulatedTse", forcedOutage = false });
        (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created);
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Json;
        (await shop.PostAsync($"/api/pos/sessions/{session["id"]}/cash-movements", new { kind = "Deposit", amountMinor = 1_00, reasonCode = "ChangeFund" }))
            .Expect(HttpStatusCode.Created);
        var stranger = await _scenario.RegisterAsync("fremd", wholesaler: false);

        string[] tables = ["fiscal_configurations", "simulated_tses", "simulated_tse_transactions", "fiscal_outages", "fiscal_records"];
        async Task<long> CountAs(Guid? org, string table)
        {
            await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
            await connection.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection))
            {
                set.Parameters.AddWithValue("org", org?.ToString() ?? string.Empty);
                await set.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM pos.{table} WHERE organization_id = '{shop.OrganizationId}'", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        foreach (var table in tables)
        {
            Assert.True(await CountAs(shop.OrganizationId, table) > 0, table);
            Assert.Equal(0, await CountAs(stranger.OrganizationId, table));
            Assert.Equal(0, await CountAs(null, table));
        }

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_org', '{shop.OrganizationId}', false)", app))
            await set.ExecuteNonQueryAsync();
        foreach (var table in tables)
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM pos.{table}", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var update = new NpgsqlCommand("UPDATE pos.fiscal_records SET status = 'Failed'", app))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        // Writing into another tenant is refused by the policy.
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO pos.fiscal_configurations (id, organization_id, mode, updated_at, updated_by)
            VALUES ('{Guid.NewGuid()}', '{stranger.OrganizationId}', 'Disabled', now(), '{Guid.NewGuid()}')
            """, app))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Dsfinvk_export_contains_the_closed_sessions_receipts_and_tse_data()
    {
        var (shop, register, product) = await ShopAsync();
        await ConfigureAsync(shop, new { mode = "SimulatedTse" });
        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 23_80), Key())).Expect(HttpStatusCode.Created).Json;
        var storno = (await shop.PostAsync($"/api/pos/sales/{sale["id"]}/storno", new { reason = "Irrtum" })).Expect(HttpStatusCode.Created).Json;
        (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, "Card"), Key())).Expect(HttpStatusCode.Created);
        var session = (await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Json;
        (await shop.PostAsync($"/api/pos/sessions/{session["id"]}/cash-movements", new { kind = "Withdrawal", amountMinor = 20_00, reasonCode = "BankDeposit" }))
            .Expect(HttpStatusCode.Created);
        (await shop.PostAsync($"/api/pos/sessions/{session["id"]}/close", new { countedCashMinor = 80_00 })).Expect(HttpStatusCode.OK);

        var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        Assert.Equal("pos.export_range", (await shop.GetAsync($"/api/pos/exports/dsfinvk?registerId={register}&from={tomorrow}&to={yesterday}")).Code);
        var file = await shop.GetFileAsync($"/api/pos/exports/dsfinvk?registerId={register}&from={yesterday}&to={tomorrow}");
        Assert.Equal(HttpStatusCode.OK, file.Status);
        Assert.Equal("application/zip", file.MediaType);

        using var zip = new ZipArchive(new MemoryStream(file.Body));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        string[] expected = ["transactions.csv", "transactions_vat.csv", "transactions_tse.csv", "datapayment.csv", "references.csv", "lines.csv", "lines_vat.csv",
            "cashpointclosing.csv", "location.csv", "cashregister.csv", "vat.csv", "tse.csv", "businesscases.csv", "payment.csv", "cash_per_currency.csv",
            "index.xml", "LIESMICH.txt"];
        Assert.Equal(expected.Order(), zip.Entries.Select(e => e.FullName).Order());

        var transactions = Read("transactions.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Z_KASSE_ID,Z_ERSTELLUNG,Z_NR,BON_ID,BON_NR,BON_TYP", transactions[0]);
        Assert.Equal(4, transactions.Length); // header + 3 receipts
        Assert.Contains(transactions, l => l.Contains($"\"{storno["id"]}\"") && l.Contains(",1,") && l.Contains("-23.80"));
        Assert.All(transactions.Skip(1), l => Assert.Contains($"\"{register:N}\",", l));
        var tseRows = Read("transactions_tse.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, tseRows.Length);
        Assert.Contains(tseRows, l => l.Contains(sale["fiscal"]!["signature"]!.GetValue<string>()) && l.Contains("Beleg^23.80_0.00_0.00_0.00_0.00^23.80:Bar"));
        var tse = Read("tse.csv");
        Assert.Contains(sale["fiscal"]!["tseSerialNumber"]!.GetValue<string>(), tse);
        Assert.Contains("TEST-TSE", tse);
        Assert.Contains("\"Geldtransit\",\"\",,5,-20.00,-20.00,0.00", Read("businesscases.csv"));
        Assert.DoesNotContain("\"Bar\"", Read("payment.csv")); // 23.80 sold, 23.80 cancelled: no cash total
        Assert.Contains("\"Unbar\",\"Karte\",10.00", Read("payment.csv"));
        Assert.Contains("\"Transaktion\"", Read("references.csv"));
        var closing = Read("cashpointclosing.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, closing.Length);
        Assert.Contains(",1,", closing[1]); // Z_NR 1
        var index = Read("index.xml");
        Assert.Contains("<URL>transactions.csv</URL>", index);
        Assert.Contains("<Name>Bonkopf</Name>", index);
        Assert.Contains("nicht zertifiziert", index);
        Assert.Contains("NICHT zertifiziert", Read("LIESMICH.txt"));
        Assert.Contains("TEST-TSE", Read("LIESMICH.txt"));
    }

    [Fact]
    public async Task Production_refuses_the_simulated_tse_unless_staging_allows_it()
    {
        WebApplicationFactory<Program> Production(bool allow) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Production");
            web.UseSetting("ConnectionStrings:HandyNet", fixture.AppConnectionString);
            web.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            web.UseSetting("Pos:FiscalSigner", "SimulatedTse");
            if (allow)
                web.UseSetting("Pos:Fiscal:AllowSimulatedTseInProduction", "true");
            web.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

        await using (var refused = Production(allow: false))
        {
            var ex = Assert.ThrowsAny<Exception>(() => refused.Server);
            Assert.Contains("SimulatedTse fiscal signer", ex.ToString());
        }
        await using var staging = Production(allow: true);
        Assert.Equal(HttpStatusCode.OK, (await staging.CreateClient().GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Fiskaly_connection_test_client_registration_and_a_signed_sale()
    {
        var (shop, register, product) = await ShopAsync();
        var admin = await _scenario.AdminAsync();
        var tss = Guid.NewGuid();
        var test = $"/api/admin/pos/fiscal/{shop.OrganizationId}/fiskaly/test";

        // No credentials yet: the test reports it without calling fiskaly.
        await ConfigureAsync(shop, new { mode = "FiskalyCloudTse", fiskalyTssId = tss });
        var missing = (await admin.PostAsync(test)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Failed", "fiskaly.credentialsMissing"), (missing["status"]!.GetValue<string>(), missing["errorCode"]!.GetValue<string>()));

        await ConfigureAsync(shop, new { mode = "FiskalyCloudTse", fiskalyTssId = tss, fiskalyApiKey = "test_key_fx", fiskalyApiSecret = "secret_fx" });
        var result = (await admin.PostAsync(test)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Warning", "TEST", false), (result["status"]!.GetValue<string>(), result["environment"]!.GetValue<string>(),
            result["environmentMismatch"]!.GetValue<bool>()));
        Assert.Equal("INITIALIZED", result["tssState"]!.GetValue<string>());
        var client = result["clients"]!.AsArray().Single()!;
        Assert.Equal((register, "NotRegistered"), (Id(client, "registerId"), client["state"]!.GetValue<string>()));
        var clientId = Guid.Parse(client["clientId"]!.GetValue<string>());
        Assert.Equal('4', clientId.ToString()[14]); // UUIDv4 derived from the register id
        Assert.DoesNotContain("secret_fx", result.ToJsonString());
        // The stored, encrypted credentials were used for POST /auth.
        Assert.Contains(fixture.Fiskaly.Requests, r => r.Path == "/api/v2/auth" && r.Body!.Contains("test_key_fx"));

        // A till that is not registered is refused (outage path), never registered implicitly from the till.
        var refused = (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Failed", refused["fiscal"]!["status"]!.GetValue<string>());

        // Registration needs the TSS admin PIN: validated, a wrong PIN is reported, the right one registers the client.
        var registerPath = $"/api/admin/pos/fiscal/{shop.OrganizationId}/registers/{register}/fiskaly-client";
        Assert.Equal("validation.failed", (await admin.PostAsync(registerPath, new { adminPin = "12" })).Code);
        Assert.Equal("pos.fiskaly_admin_pin_rejected", (await admin.PostAsync(registerPath, new { adminPin = "999999" })).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.PostAsync(registerPath, new { adminPin = FakeFiskalyServer.AdminPin })).Status);
        var registered = (await admin.PostAsync(registerPath, new { adminPin = FakeFiskalyServer.AdminPin })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Registered", clientId.ToString()), (registered["state"]!.GetValue<string>(), registered["clientId"]!.GetValue<string>()));
        Assert.Contains(fixture.Fiskaly.For(tss), r => r.Method == "PUT" && r.Path.Contains("/client/") && r.Body!.Contains(register.ToString("N")));

        var txBefore = fixture.Fiskaly.For(tss).Count(r => r.Path.Contains("/tx/"));
        var ok = (await admin.PostAsync(test)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Ok", ok["status"]!.GetValue<string>());
        Assert.Equal(txBefore, fixture.Fiskaly.For(tss).Count(r => r.Path.Contains("/tx/"))); // the connection test signs nothing

        // Now a sale is signed through fiskaly: test environment marked on the receipt and the till.
        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, product), Key())).Expect(HttpStatusCode.Created).Json;
        var fiscal = sale["fiscal"]!;
        Assert.Equal(("Signed", true, "FiskalyCloudTse"), (fiscal["status"]!.GetValue<string>(), fiscal["isTest"]!.GetValue<bool>(),
            fiscal["mode"]!.GetValue<string>()));
        Assert.Equal(clientId.ToString(), fiscal["clientId"]!.GetValue<string>());
        Assert.StartsWith("V0;", fiscal["qrPayload"]!.GetValue<string>());
        Assert.Equal(2, fixture.Fiskaly.For(tss).Count(r => r.Path.Contains("/tx/")) - txBefore); // revision 1 + 2
        var finish = fixture.Fiskaly.For(tss).Last(r => r.Path.Contains("tx_revision=2"));
        var receipt = JsonNode.Parse(finish.Body!)!["schema"]!["standard_v1"]!["receipt"]!;
        Assert.Equal("19.00", receipt["amounts_per_vat_rate"]![0]!["amount"]!.GetValue<string>());
        var status = (await shop.GetAsync($"/api/pos/registers/{register}/fiscal")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Test", true), (status["state"]!.GetValue<string>(), status["isTest"]!.GetValue<bool>()));

        // Configured as Production while the keys are TEST keys: the connection test flags the mismatch.
        await ConfigureAsync(shop, new { mode = "FiskalyCloudTse", fiskalyTssId = tss, fiskalyEnvironment = "Production" });
        var mismatch = (await admin.PostAsync(test)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Warning", true), (mismatch["status"]!.GetValue<string>(), mismatch["environmentMismatch"]!.GetValue<bool>()));
    }
}
