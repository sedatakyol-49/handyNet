using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Notifications.Application;
using HandyNet.Persistence;
using HandyNet.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// POS Phase 2c open items: product images on tiles, cashier switching with PIN, manager approval, e-receipts, invoices
/// for POS sales and cash rounding (real PostgreSQL, RLS on).
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosStaffAndDocumentsTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);
    private const string StaffPassword = "Kassierer-Passwort-1";

    private static Dictionary<string, string> Key() => new() { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") };

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

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

    private static async Task<Guid> StockProductAsync(ApiClient shop, int quantity, string name)
    {
        var product = (await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = $"{name} {Scenario.UniqueSuffix()}", category = "Accessory", quality = "Aftermarket", ean = Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json;
        var productId = Id(product);
        var warehouseId = await DefaultWarehouseAsync(shop);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId, delta = quantity, reason = "Wareneingang", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        return productId;
    }

    private static async Task<Guid> RegisterAsync(ApiClient shop, string name = "Kasse 1") =>
        Id((await shop.PostAsync("/api/pos/registers", new { name })).Expect(HttpStatusCode.Created).Json);

    private static async Task<Guid> OpenSessionAsync(ApiClient client, Guid register, long openingCashMinor = 100_00) =>
        Id((await client.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor })).Expect(HttpStatusCode.Created).Json);

    private static async Task<ApiResponse> UploadImageAsync(ApiClient client, Guid productId, byte[] data)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "foto.png");
        return await client.SendAsync(HttpMethod.Post, $"/api/catalog/products/{productId}/images", content: form);
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(ApiClient Client, Guid UserId, string Email)> StaffAsync(ApiClient owner, string role = "Staff", string name = "Kassiererin")
    {
        var email = $"kasse-{Scenario.UniqueSuffix()}@test.de";
        var userId = Id((await owner.PostAsync("/api/organization/staff", new { email, displayName = name, password = StaffPassword, role }))
            .Expect(HttpStatusCode.OK).Json, "userId");
        var client = fixture.CreateClient();
        await client.LoginAsync(email, StaffPassword);
        client.OrganizationId = owner.OrganizationId;
        return (client, userId, email);
    }

    private static Guid UserIdOf(ApiClient client) =>
        Guid.Parse(System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(Pad(client.Token!.Split('.')[1]))).RootElement.GetProperty("sub").GetString()!);

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }

    private static Dictionary<string, string> As(string cashierToken, Dictionary<string, string>? more = null)
    {
        var headers = more ?? new Dictionary<string, string>();
        headers["X-Pos-Cashier"] = cashierToken;
        return headers;
    }

    private static async Task<string> SignInAsync(ApiClient device, Guid register, Guid userId, string pin) =>
        (await device.PostAsync($"/api/pos/registers/{register}/cashier", new { userId, pin })).Expect(HttpStatusCode.OK).Json["token"]!.GetValue<string>();

    private static object Sale(Guid register, Guid product, long price, long discount = 0) => new
    {
        registerId = register,
        lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = price, discountMinor = discount } },
        payments = new[] { new { method = "Cash", amountMinor = price - discount } },
    };

    // ---- 2. PINs and cashier switching ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Pins_are_set_with_password_or_by_the_owner_hashed_and_listed_for_the_till()
    {
        var shop = await PosShopAsync();
        var (staff, staffId, _) = await StaffAsync(shop);
        var ownerId = UserIdOf(shop);

        // Own PIN needs the account password; trivial PINs are refused.
        Assert.Equal("pos.password_invalid", (await staff.PutAsync("/api/pos/me/pin", new { pin = "4711", currentPassword = "falsch-falsch-1" })).Code);
        Assert.Equal("pos.pin_too_simple", (await staff.PutAsync("/api/pos/me/pin", new { pin = "1234", currentPassword = StaffPassword })).Code);
        Assert.Equal("pos.pin_format", (await staff.PutAsync("/api/pos/me/pin", new { pin = "12", currentPassword = StaffPassword })).Code);
        Assert.True((await staff.PutAsync("/api/pos/me/pin", new { pin = "4711", currentPassword = StaffPassword })).Expect(HttpStatusCode.OK)
            .Json["hasPin"]!.GetValue<bool>());
        Assert.True((await staff.GetAsync("/api/pos/me/pin")).Expect(HttpStatusCode.OK).Json["hasPin"]!.GetValue<bool>());

        // The owner sets a PIN for himself on the Team page endpoint; staff may not.
        (await shop.PutAsync($"/api/pos/cashiers/{ownerId}/pin", new { pin = "2580" })).Expect(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsync($"/api/pos/cashiers/{ownerId}/pin", new { pin = "9999" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await shop.PutAsync($"/api/pos/cashiers/{Guid.NewGuid()}/pin", new { pin = "2580" })).Status);

        var pins = (await shop.GetAsync("/api/pos/pins")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.All(pins, p => Assert.True(p!["hasPin"]!.GetValue<bool>()));
        var cashiers = (await staff.GetAsync("/api/pos/cashiers")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(new[] { ownerId, staffId }.Order(), cashiers.Select(c => Id(c!, "userId")).Order());

        // Stored as salted PBKDF2, never in plain text, and not copied into the audit log.
        var hash = await AsOwnerAsync<string>($"SELECT pin_hash FROM pos.cashier_pins WHERE user_id = '{staffId}'");
        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.DoesNotContain("4711", hash);
        Assert.Equal(0L, await AsOwnerAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE changes::text LIKE '%pbkdf2%'"));

        // Clearing removes the staff member from the till list.
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = (string?)null })).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain((await staff.GetAsync("/api/pos/cashiers")).Expect(HttpStatusCode.OK).Json.AsArray(), c => Id(c!, "userId") == staffId);
    }

    [Fact]
    public async Task Switched_cashier_is_recorded_on_receipts_and_their_own_rights_apply()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var product = await StockProductAsync(shop, 10, "Kabel");
        (await shop.PutAsync($"/api/pos/prices/{product}", new { priceMinor = 10_00 })).Expect(HttpStatusCode.OK);
        var (_, staffId, _) = await StaffAsync(shop, name: "Kassiererin Ada");
        (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = Array.Empty<string>(), maxDiscountPercent = (int?)null }))
            .Expect(HttpStatusCode.OK);
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = "4711" })).Expect(HttpStatusCode.OK);
        await OpenSessionAsync(shop, register);

        // The owner's device switches to the cashier with her PIN.
        var signIn = (await shop.PostAsync($"/api/pos/registers/{register}/cashier", new { userId = staffId, pin = "4711" })).Expect(HttpStatusCode.OK).Json;
        var token = signIn["token"]!.GetValue<string>();
        Assert.Equal(staffId, Id(signIn["cashier"]!, "userId"));
        Assert.Equal("Kassiererin Ada", signIn["cashier"]!["displayName"]!.GetValue<string>());
        Assert.DoesNotContain("pos.discount", signIn["cashier"]!["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal(staffId, Id((await shop.SendAsync(HttpMethod.Get, "/api/pos/cashier", headers: As(token))).Expect(HttpStatusCode.OK).Json, "userId"));

        // Her rights apply although the device user (owner) could do it: no discount, no storno, no closing.
        Assert.Equal("pos.discount_forbidden", (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1_00), As(token, Key()))).Code);
        var receipt = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00), As(token, Key()))).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(staffId, Id(receipt, "createdBy"));
        Assert.Equal("Kassiererin Ada", receipt["createdByName"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await shop.SendAsync(HttpMethod.Post, $"/api/pos/sales/{Id(receipt)}/storno", new { reason = "Test" }, headers: As(token))).Status);
        var session = Id((await shop.SendAsync(HttpMethod.Get, $"/api/pos/registers/{register}/sessions/current", headers: As(token))).Json);
        Assert.Equal("permission.denied",
            (await shop.SendAsync(HttpMethod.Post, $"/api/pos/sessions/{session}/close", new { countedCashMinor = 0 }, headers: As(token))).Code);

        // Without the header the device acts as its own login again (the owner): discount allowed, recorded as owner.
        var own = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00, 1_00), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(UserIdOf(shop), Id(own, "createdBy"));

        // Locking ends the cashier sign-in: the token is refused afterwards.
        (await shop.SendAsync(HttpMethod.Post, "/api/pos/cashier/lock", new { }, headers: As(token))).Expect(HttpStatusCode.NoContent);
        Assert.Equal("pos.cashier_invalid", (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00), As(token, Key()))).Code);
        Assert.Equal("Locked", await AsOwnerAsync<string>($"SELECT ended_reason FROM pos.cashier_sessions WHERE cashier_user_id = '{staffId}'"));
    }

    [Fact]
    public async Task Wrong_pins_lock_the_member_and_the_owner_can_reset()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var (_, staffId, _) = await StaffAsync(shop);
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = "4711" })).Expect(HttpStatusCode.OK);

        for (var remaining = 4; remaining >= 1; remaining--)
        {
            var wrong = await shop.PostAsync($"/api/pos/registers/{register}/cashier", new { userId = staffId, pin = "0815" });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);
            Assert.Equal("pos.pin_invalid", wrong.Code);
            Assert.Equal(remaining, wrong.Json["remainingAttempts"]!.GetValue<int>());
        }
        Assert.Equal("pos.pin_locked", (await shop.PostAsync($"/api/pos/registers/{register}/cashier", new { userId = staffId, pin = "0815" })).Code);
        // Locked: even the right PIN is refused, and the lock is stored (survives restarts).
        Assert.Equal("pos.pin_locked", (await shop.PostAsync($"/api/pos/registers/{register}/cashier", new { userId = staffId, pin = "4711" })).Code);
        Assert.True(await AsOwnerAsync<bool>($"SELECT locked_until > now() FROM pos.cashier_pins WHERE user_id = '{staffId}'"));
        Assert.True((await shop.GetAsync("/api/pos/cashiers")).Json.AsArray().Single(c => Id(c!, "userId") == staffId)!["locked"]!.GetValue<bool>());

        // The owner sets a new PIN: unlocked.
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = "2580" })).Expect(HttpStatusCode.OK);
        await SignInAsync(shop, register, staffId, "2580");
        Assert.Equal("pos.pin_not_set",
            (await shop.PostAsync($"/api/pos/registers/{register}/cashier", new { userId = UserIdOf(shop), pin = "2580" })).Code);
    }

    [Fact]
    public async Task Cashier_token_is_bound_to_organization_device_login_register_and_inactivity()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop, "Kasse A");
        var other = await RegisterAsync(shop, "Kasse B");
        var product = await StockProductAsync(shop, 10, "Hülle");
        var (staff, staffId, _) = await StaffAsync(shop);
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = "4711" })).Expect(HttpStatusCode.OK);
        await OpenSessionAsync(shop, register);
        await OpenSessionAsync(shop, other);
        var token = await SignInAsync(shop, register, staffId, "4711");

        // Another register of the same organization: refused.
        Assert.Equal("pos.cashier_register_mismatch", (await shop.PostAsync("/api/pos/sales", Sale(other, product, 5_00), As(token, Key()))).Code);

        // Another device login (the cashier's own account) cannot use the token.
        Assert.Equal("pos.cashier_invalid", (await staff.PostAsync("/api/pos/sales", Sale(register, product, 5_00), As(token, Key()))).Code);

        // A second organization of the same device user (owner of both): the token does not exist there (RLS).
        var firstOrg = shop.OrganizationId;
        var secondOrg = Id((await shop.PostAsync("/api/organizations", new
        {
            name = $"Filiale {Scenario.UniqueSuffix()}", legalName = "Filiale GmbH", street = "Nebenstraße 2", postalCode = "10117", city = "Berlin",
            isWholesaler = false, isRetailer = true, isRepairProvider = false,
        })).Expect(HttpStatusCode.Created).Json);
        shop.OrganizationId = secondOrg;
        await _scenario.ApproveAsync(shop, "Pos", "Inventory");
        Assert.Equal("pos.cashier_invalid", (await shop.SendAsync(HttpMethod.Get, "/api/pos/cashier", headers: As(token))).Code);
        shop.OrganizationId = firstOrg;

        // Sales on the bound register work; then the register's auto-lock expires the idle sign-in.
        (await shop.PostAsync("/api/pos/sales", Sale(register, product, 5_00), As(token, Key()))).Expect(HttpStatusCode.Created);
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse A", isActive = true, autoLockMinutes = 5 })).Expect(HttpStatusCode.OK);
        Assert.Equal(5, (await shop.GetAsync($"/api/pos/registers/{register}")).Json["autoLockMinutes"]!.GetValue<int>());
        await AsOwnerAsync<int>($"UPDATE pos.cashier_sessions SET last_seen_at = now() - interval '6 minutes' "
                                + $"WHERE cashier_user_id = '{staffId}' RETURNING 1");
        Assert.Equal("pos.cashier_invalid", (await shop.PostAsync("/api/pos/sales", Sale(register, product, 5_00), As(token, Key()))).Code);

        // A new sign-in replaces the earlier one of the same device login and register.
        var first = await SignInAsync(shop, register, staffId, "4711");
        var replacement = await SignInAsync(shop, register, staffId, "4711");
        Assert.Equal("pos.cashier_invalid", (await shop.SendAsync(HttpMethod.Get, "/api/pos/cashier", headers: As(first))).Code);
        (await shop.SendAsync(HttpMethod.Get, "/api/pos/cashier", headers: As(replacement))).Expect(HttpStatusCode.OK);

        // A garbage token never reaches the database lookup and is refused the same way.
        Assert.Equal("pos.cashier_invalid", (await shop.SendAsync(HttpMethod.Get, "/api/pos/registers", headers: As("nonsense"))).Code);
    }

    [Fact]
    public async Task Pins_and_cashier_sessions_are_tenant_isolated_and_never_deleted()
    {
        var shop = await PosShopAsync();
        var (_, staffId, _) = await StaffAsync(shop);
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = "4711" })).Expect(HttpStatusCode.OK);
        var register = await RegisterAsync(shop);
        await SignInAsync(shop, register, staffId, "4711");

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        async Task<long> CountAsync(Guid org, string table)
        {
            await using var tx = await app.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_org', '{org}', true)", app, tx))
                await set.ExecuteScalarAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM pos.{table}", app, tx);
            var count = (long)(await command.ExecuteScalarAsync())!;
            await tx.RollbackAsync();
            return count;
        }
        Assert.Equal(1, await CountAsync(shop.OrganizationId!.Value, "cashier_pins"));
        Assert.Equal(1, await CountAsync(shop.OrganizationId!.Value, "cashier_sessions"));
        Assert.Equal(0, await CountAsync(Guid.NewGuid(), "cashier_pins"));
        Assert.Equal(0, await CountAsync(Guid.NewGuid(), "cashier_sessions"));

        foreach (var table in new[] { "cashier_pins", "cashier_sessions" })
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM pos.{table}", app);
            var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal("42501", error.SqlState);
        }
        // The session identity is fixed even for the owner role.
        var change = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<int>($"UPDATE pos.cashier_sessions SET cashier_user_id = device_user_id WHERE cashier_user_id = '{staffId}' RETURNING 1"));
        Assert.Equal("42501", change.SqlState);
    }

    // ---- 3. manager approval by PIN -----------------------------------------------------------------------------------------

    private static Dictionary<string, string> Approved(string approvalToken, Dictionary<string, string>? more = null)
    {
        var headers = more ?? new Dictionary<string, string>();
        headers["X-Pos-Approval"] = approvalToken;
        return headers;
    }

    private async Task<(ApiClient Shop, Guid Register, Guid Product, Guid StaffId, string StaffToken, Guid ManagerId)> ApprovalSetupAsync()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var product = await StockProductAsync(shop, 20, "Panzerglas");
        (await shop.PutAsync($"/api/pos/prices/{product}", new { priceMinor = 20_00 })).Expect(HttpStatusCode.OK);
        var (_, staffId, _) = await StaffAsync(shop, name: "Kassierer Ben");
        var (_, managerId, _) = await StaffAsync(shop, "Manager", "Managerin Cleo");
        (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = Array.Empty<string>(), maxDiscountPercent = (int?)null }))
            .Expect(HttpStatusCode.OK);
        (await shop.PutAsync($"/api/pos/cashiers/{staffId}/pin", new { pin = "4711" })).Expect(HttpStatusCode.OK);
        (await shop.PutAsync($"/api/pos/cashiers/{managerId}/pin", new { pin = "2580" })).Expect(HttpStatusCode.OK);
        await OpenSessionAsync(shop, register);
        var staffToken = await SignInAsync(shop, register, staffId, "4711");
        return (shop, register, product, staffId, staffToken, managerId);
    }

    private static async Task<ApiResponse> ApproveAsync(ApiClient device, Guid register, string cashierToken, Guid approver, string pin, params string[] permissions) =>
        await device.PostAsync($"/api/pos/registers/{register}/approvals", new { approverUserId = approver, pin, permissions }, As(cashierToken));

    [Fact]
    public async Task Manager_pin_approves_a_discount_once_and_the_receipt_records_who_approved()
    {
        var (shop, register, product, staffId, staffToken, managerId) = await ApprovalSetupAsync();
        Assert.Equal("pos.discount_forbidden", (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, 5_00), As(staffToken, Key()))).Code);

        // Only owners/managers approve; never the cashier for themselves; the PIN must be right.
        Assert.Equal("pos.approver_not_manager", (await ApproveAsync(shop, register, staffToken, staffId, "4711", "pos.discount")).Code);
        Assert.Equal("pos.pin_invalid", (await ApproveAsync(shop, register, staffToken, managerId, "9999", "pos.discount")).Code);
        Assert.Equal("pos.approval_permissions", (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.manage")).Code);
        var approval = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.discount")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Managerin Cleo", approval["approverName"]!.GetValue<string>());
        var approvalToken = approval["token"]!.GetValue<string>();

        var receipt = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, 5_00), As(staffToken, Approved(approvalToken, Key()))))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal(staffId, Id(receipt, "createdBy"));
        Assert.Equal(managerId, Id(receipt["approval"]!, "approverUserId"));
        Assert.Equal("Managerin Cleo", receipt["approval"]!["approverName"]!.GetValue<string>());
        Assert.Contains("Freigabe: Managerin Cleo", receipt["document"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Managerin Cleo", (await shop.GetAsync($"/api/pos/sales/{Id(receipt)}")).Json["approval"]!["approverName"]!.GetValue<string>());

        // Single use: the same approval cannot allow a second discount.
        Assert.Equal("pos.approval_invalid",
            (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, 5_00), As(staffToken, Approved(approvalToken, Key())))).Code);
        Assert.Equal("Sale", await AsOwnerAsync<string>($"SELECT used_for FROM pos.approvals WHERE receipt_id = '{Id(receipt)}'"));
        // Audited (granted and used).
        Assert.True(await AsOwnerAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE entity_type LIKE '%PosApproval' AND entity_id LIKE '%{Id(approval, "approvalId")}%'") >= 2);
    }

    [Fact]
    public async Task Approvals_allow_refund_cash_movement_and_closing_and_are_recorded()
    {
        var (shop, register, product, _, staffToken, managerId) = await ApprovalSetupAsync();
        var sale = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00), As(staffToken, Key()))).Expect(HttpStatusCode.Created).Json;
        var session = Id((await shop.GetAsync($"/api/pos/registers/{register}/sessions/current")).Json);

        // Refund
        var returnBody = new { lines = new[] { new { lineNumber = 1, quantity = 1 } }, refundMethod = "Cash", reason = "Defekt", registerId = register };
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/returns", returnBody, As(staffToken, Key()))).Status);
        var refundApproval = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.refund")).Expect(HttpStatusCode.Created).Json;
        var refund = (await shop.PostAsync($"/api/pos/sales/{Id(sale)}/returns", returnBody,
            As(staffToken, Approved(refundApproval["token"]!.GetValue<string>(), Key())))).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(managerId, Id(refund["approval"]!, "approverUserId"));

        // Cash movement
        var movementBody = new { kind = "Deposit", amountMinor = 50_00, reasonCode = "ChangeFund" };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await shop.SendAsync(HttpMethod.Post, $"/api/pos/sessions/{session}/cash-movements", movementBody, headers: As(staffToken))).Status);
        var cashApproval = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.cash_movement")).Json["token"]!.GetValue<string>();
        var movement = (await shop.SendAsync(HttpMethod.Post, $"/api/pos/sessions/{session}/cash-movements", movementBody,
            headers: As(staffToken, Approved(cashApproval)))).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Managerin Cleo", movement["approval"]!["approverName"]!.GetValue<string>());

        // An approval for one right does not allow another (closing needs its own approval).
        var wrong = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.refund")).Json["token"]!.GetValue<string>();
        Assert.Equal("permission.denied", (await shop.SendAsync(HttpMethod.Post, $"/api/pos/sessions/{session}/close", new { countedCashMinor = 0 },
            headers: As(staffToken, Approved(wrong)))).Code);
        var closeApproval = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.close_session")).Json["token"]!.GetValue<string>();
        var closed = (await shop.SendAsync(HttpMethod.Post, $"/api/pos/sessions/{session}/close", new { countedCashMinor = 150_00 },
            headers: As(staffToken, Approved(closeApproval)))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(managerId, Id(closed["closingApproval"]!, "approverUserId"));
        Assert.Equal("Managerin Cleo", closed["movements"]!.AsArray().Single()!["approval"]!["approverName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approval_is_bound_to_register_device_cashier_and_expires()
    {
        var (shop, register, product, staffId, staffToken, managerId) = await ApprovalSetupAsync();
        var other = await RegisterAsync(shop, "Kasse 2");
        await OpenSessionAsync(shop, other);
        var otherToken = await SignInAsync(shop, other, staffId, "4711");

        // Another register: refused (also when the cashier is signed in there).
        var token = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.discount")).Json["token"]!.GetValue<string>();
        Assert.Equal("pos.approval_invalid",
            (await shop.PostAsync("/api/pos/sales", Sale(other, product, 20_00, 1_00), As(otherToken, Approved(token, Key())))).Code);

        // Another cashier on the same device (here: the device user without cashier token) cannot use it.
        Assert.Equal("pos.approval_invalid", (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, 1_00), Approved(token, Key()))).Code);

        // Expired: refused.
        await AsOwnerAsync<int>($"""
            SET session_replication_role = replica;
            UPDATE pos.approvals SET created_at = now() - interval '10 minutes', expires_at = now() - interval '1 minute'
            WHERE approver_user_id = '{managerId}';
            SET session_replication_role = origin;
            SELECT 1;
            """);
        Assert.Equal("pos.approval_invalid",
            (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, 1_00), As(staffToken, Approved(token, Key())))).Code);

        // The database refuses a second use and any change of a used approval, even for the owner role.
        var fresh = (await ApproveAsync(shop, register, staffToken, managerId, "2580", "pos.discount")).Json["token"]!.GetValue<string>();
        var receipt = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 20_00, 1_00), As(staffToken, Approved(fresh, Key()))))
            .Expect(HttpStatusCode.Created).Json;
        var change = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<int>($"UPDATE pos.approvals SET used_for = 'Return' WHERE receipt_id = '{Id(receipt)}' RETURNING 1"));
        Assert.Equal("42501", change.SqlState);
    }

    // ---- 4. e-receipts ---------------------------------------------------------------------------------------------------

    private async Task ProcessEmailsUntilAsync(Func<bool> done)
    {
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        var email = fixture.Factory.Services.GetRequiredService<EmailDeliveryWorker>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "Expected emails were not delivered within 30 s.");
            await outbox.DispatchBatchAsync(CancellationToken.None);
            await email.DeliverBatchAsync(CancellationToken.None);
            await Task.Delay(100);
        }
    }

    private async Task<(ApiClient Shop, Guid Register, JsonNode Receipt, string CustomerEmail)> SaleWithCustomerAsync()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var product = await StockProductAsync(shop, 5, "Ladegerät");
        var customerEmail = $"kundin-{Scenario.UniqueSuffix()}@example.de";
        var customer = (await shop.PostAsync("/api/pos/customers", new
        {
            name = "Erika Mustermann", email = customerEmail, street = "Lindenweg 3", postalCode = "10405", city = "Berlin", countryCode = "DE",
        })).Expect(HttpStatusCode.Created).Json;
        await OpenSessionAsync(shop, register);
        var receipt = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            customerId = Id(customer),
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 29_99, discountMinor = 0, note = "mit Kabel <b>" } },
            payments = new[] { new { method = "Cash", amountMinor = 30_00 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        return (shop, register, receipt, customerEmail);
    }

    [Fact]
    public async Task Receipt_is_emailed_through_the_notifications_outbox_as_text_and_html()
    {
        var (shop, _, receipt, customerEmail) = await SaleWithCustomerAsync();
        var receiptId = Id(receipt);

        // Without an address: the customer's email; the response does not echo the full address.
        var sent = (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = (string?)null })).Expect(HttpStatusCode.Accepted).Json;
        Assert.DoesNotContain(customerEmail, sent.ToJsonString());
        Assert.StartsWith("k***@", sent["recipientMasked"]!.GetValue<string>());
        var other = $"privat-{Scenario.UniqueSuffix()}@example.de";
        (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = other })).Expect(HttpStatusCode.Accepted);
        Assert.Equal("pos.receipt_email_invalid", (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = "kein-at" })).Code);

        await ProcessEmailsUntilAsync(() => fixture.Emails.Sent.Any(e => e.To == customerEmail) && fixture.Emails.Sent.Any(e => e.To == other));
        var mail = fixture.Emails.Sent.Single(e => e.To == customerEmail);
        Assert.StartsWith($"Kassenbeleg Nr. {receipt["number"]}", mail.Subject);
        Assert.Contains("29,99 €", mail.Body);
        Assert.Contains("Rückgeld 0,01 €", mail.Body);
        Assert.DoesNotContain("Erika", mail.Body); // no customer block in the e-receipt
        var html = fixture.Emails.SentHtml.Single(e => e.To == customerEmail).Html;
        Assert.Contains("mit Kabel &lt;b&gt;", html); // encoded
        Assert.DoesNotContain("<b>", html);

        // Recorded per receipt (address kept out of the audit log), at most 5 per receipt.
        Assert.Equal(2L, await AsOwnerAsync<long>($"SELECT count(*) FROM pos.receipt_emails WHERE receipt_id = '{receiptId}'"));
        Assert.Equal(0L, await AsOwnerAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE changes::text LIKE '%{customerEmail}%'"));
        for (var i = 0; i < 3; i++)
            (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = other })).Expect(HttpStatusCode.Accepted);
        Assert.Equal("pos.receipt_email_limit", (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = other })).Code);

        // Another organization cannot email (or even see) the receipt.
        var foreign = await PosShopAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = other })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/api/pos/sales/{receiptId}/public-link")).Status);
    }

    [Fact]
    public async Task Receipt_email_uses_the_company_email_language_unless_the_request_names_one()
    {
        var (shop, _, receipt, _) = await SaleWithCustomerAsync();
        var receiptId = Id(receipt);
        var number = receipt["number"]!.ToString();
        var viaCompany = $"firma-{Scenario.UniqueSuffix()}@example.de";
        var viaRequest = $"anfrage-{Scenario.UniqueSuffix()}@example.de";

        await shop.SetEmailLanguageAsync("en");
        (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = viaCompany })).Expect(HttpStatusCode.Accepted);
        (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = viaRequest, language = "ru" })).Expect(HttpStatusCode.Accepted);
        // Unknown language: validation error, nothing queued.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync($"/api/pos/sales/{receiptId}/email", new { email = viaRequest, language = "xx" })).Status);

        await ProcessEmailsUntilAsync(() => fixture.Emails.Sent.Any(e => e.To == viaCompany) && fixture.Emails.Sent.Any(e => e.To == viaRequest));
        Assert.Contains($" No. {number}", fixture.Emails.Sent.Single(e => e.To == viaCompany).Subject);
        Assert.Contains($" № {number}", fixture.Emails.Sent.Single(e => e.To == viaRequest).Subject);
        Assert.Equal(2L, await AsOwnerAsync<long>($"SELECT count(*) FROM pos.receipt_emails WHERE receipt_id = '{receiptId}'"));
    }

    [Fact]
    public async Task Public_receipt_link_is_unguessable_read_only_without_customer_data_and_expires()
    {
        var (shop, _, receipt, _) = await SaleWithCustomerAsync();
        Assert.Equal("Erika Mustermann", receipt["document"]!["customer"]!["name"]!.GetValue<string>());
        var link = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/public-link")).Expect(HttpStatusCode.Created).Json;
        var token = link["token"]!.GetValue<string>();
        Assert.Equal($"/beleg/{token}", link["path"]!.GetValue<string>());
        Assert.True(DateTimeOffset.Parse(link["expiresAt"]!.GetValue<string>()) > DateTimeOffset.UtcNow.AddDays(80));

        var anonymous = fixture.CreateClient();
        var view = (await anonymous.GetAsync($"/api/public/receipts/{token}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(receipt["number"]!.GetValue<long>(), view["document"]!["receiptNumber"]!.GetValue<long>());
        Assert.Null(view["document"]!["customer"]);
        Assert.DoesNotContain("Erika", view.ToJsonString());
        Assert.False(view["cancelled"]!.GetValue<bool>());
        // Only the hash is stored.
        Assert.Equal(0L, await AsOwnerAsync<long>($"SELECT count(*) FROM pos.receipt_links WHERE token_hash = '{token}'"));

        // Unknown, malformed and expired tokens look the same.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/public/receipts/{OpaqueTokenLike()}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/public/receipts/kurz")).Status);
        await AsOwnerAsync<int>($"""
            SET session_replication_role = replica;
            UPDATE pos.receipt_links SET created_at = now() - interval '100 days', expires_at = now() - interval '1 second' WHERE receipt_id = '{Id(receipt)}';
            SET session_replication_role = origin;
            SELECT 1;
            """);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/public/receipts/{token}")).Status);

        // The app role cannot change or delete links, and only reaches other tenants' receipts through the function.
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        await using var delete = new NpgsqlCommand("DELETE FROM pos.receipt_links", app);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        await using var read = new NpgsqlCommand("SELECT count(*) FROM pos.receipt_links", app);
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!); // no tenant set: RLS hides everything
    }

    private static string OpaqueTokenLike() => Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- 5. invoices for POS sales ----------------------------------------------------------------------------------------

    private async Task<(Guid ProductId, string Imei)> StockDeviceAsync(ApiClient shop)
    {
        var imei = NewImei();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var organizationId = shop.OrganizationId!.Value;
        scope.ServiceProvider.GetRequiredService<HandyNet.SharedKernel.Tenancy.TenantContext>().SetSystemActor(organizationId);
        var catalog = scope.ServiceProvider.GetRequiredService<HandyNet.Modules.Catalog.Contracts.IProductCatalog>();
        var inventory = scope.ServiceProvider.GetRequiredService<HandyNet.Modules.Inventory.Contracts.IInventory>();
        var product = await catalog.FindOrStageDeviceAsync("TestPhone", "Modell " + Scenario.UniqueSuffix(), "128 GB", organizationId, CancellationToken.None);
        var warehouseId = await inventory.GetOrStageDefaultWarehouseAsync(CancellationToken.None);
        var unit = await inventory.StageReceiveUnitAsync(warehouseId, product.Value.Id, imei, 100_00, HandyNet.Modules.Inventory.Domain.TaxScheme.Margin25a,
            "test:direct", CancellationToken.None);
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

    private async Task SetVatIdAsync(ApiClient shop) =>
        await AsOwnerAsync<int>($"UPDATE tenancy.organizations SET vat_id = 'DE{Random.Shared.Next(100_000_000, 999_999_999)}' "
                                + $"WHERE id = '{shop.OrganizationId}' RETURNING 1");

    [Fact]
    public async Task Invoice_for_a_sale_has_the_14_UStG_fields_a_gapless_number_and_is_issued_once()
    {
        var (shop, register, receipt, _) = await SaleWithCustomerAsync();
        var year = DateTime.UtcNow.Year;

        // Without a VAT id of the seller: refused, and no number is used up.
        Assert.Equal("pos.invoice_seller_vat_id_missing", (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Code);
        await SetVatIdAsync(shop);
        Assert.Equal(HttpStatusCode.NoContent, (await shop.GetAsync($"/api/pos/sales/{Id(receipt)}/invoice")).Status);

        var invoice = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal($"RE-{year}-000001", invoice["number"]!.GetValue<string>());
        Assert.Equal("Erika Mustermann", invoice["document"]!["customer"]!["name"]!.GetValue<string>());
        Assert.Equal(29_99, invoice["totalGrossMinor"]!.GetValue<long>());
        Assert.Equal(4_79, invoice["totalVatMinor"]!.GetValue<long>()); // 29.99 − round(29.99 / 1.19) = 4.79
        // Issued once: again returns the same invoice (200).
        var again = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(invoice), Id(again));
        Assert.Equal(Id(invoice), Id((await shop.GetAsync($"/api/pos/sales/{Id(receipt)}/invoice")).Expect(HttpStatusCode.OK).Json));

        var pdf = await shop.GetFileAsync(invoice["pdfUrl"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, pdf.Status);
        Assert.Equal("application/pdf", pdf.MediaType);
        var text = PdfText.All(pdf.Body);
        foreach (var expected in new[] { "Rechnung", $"RE-{year}-000001", "Rechnungsdatum", "Leistungsdatum", "Erika Mustermann", "Lindenweg 3", "10405 Berlin",
                     "USt-IdNr.:", "Umsatzsteuer 19 %", "4,79 €", "29,99 €" })
            Assert.Contains(expected, text);

        // An entered customer (here: a business) for a sale without customer; the next number follows without a gap.
        var product = await StockProductAsync(shop, 2, "Hülle");
        var second = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 10_00), Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("pos.invoice_customer_required", (await shop.PostAsync($"/api/pos/sales/{Id(second)}/invoice", new { })).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync($"/api/pos/sales/{Id(second)}/invoice",
            new { customer = new { name = "Firma", street = "", postalCode = "1", city = "X" } })).Status);
        var business = (await shop.PostAsync($"/api/pos/sales/{Id(second)}/invoice", new
        {
            customer = new { name = "Max Muster", company = "Muster GmbH", street = "Gewerbering 9", postalCode = "20095", city = "Hamburg", vatId = "DE 123456789" },
        })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal($"RE-{year}-000002", business["number"]!.GetValue<string>());
        Assert.Equal("DE123456789", business["document"]!["customer"]!["vatId"]!.GetValue<string>());

        // Another organization sees neither the invoice nor its PDF.
        var foreign = await PosShopAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetFileAsync(invoice["pdfUrl"]!.GetValue<string>())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Status);
    }

    [Fact]
    public async Task Invoice_shows_25a_lines_without_VAT_and_refunded_receipts_get_none()
    {
        var shop = await PosShopAsync();
        await SetVatIdAsync(shop);
        var register = await RegisterAsync(shop);
        await OpenSessionAsync(shop, register);
        var (_, imei) = await StockDeviceAsync(shop);
        var accessory = await StockProductAsync(shop, 3, "Kabel");
        var receipt = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new object[]
            {
                new { imei, quantity = 1, unitPriceMinor = 250_00, discountMinor = 0 },
                new { productId = accessory, quantity = 1, unitPriceMinor = 11_90, discountMinor = 0 },
            },
            payments = new[] { new { method = "Card", amountMinor = 261_90 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        var customer = new { name = "Erika Mustermann", street = "Lindenweg 3", postalCode = "10405", city = "Berlin" };
        var invoice = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { customer })).Expect(HttpStatusCode.Created).Json;
        var doc = invoice["document"]!;
        Assert.Equal(250_00, doc["marginGrossMinor"]!.GetValue<long>());
        Assert.Equal(1_90, invoice["totalVatMinor"]!.GetValue<long>()); // only the regularly taxed cable
        Assert.Single(doc["vat"]!.AsArray());
        Assert.Contains(doc["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("Gebrauchtgegenstände/Sonderregelung"));
        var text = PdfText.All((await shop.GetFileAsync(invoice["pdfUrl"]!.GetValue<string>())).Body);
        Assert.Contains("Differenzbesteuert (§25a)", text);
        Assert.Contains("Gebrauchtgegenstände/Sonderregelung", text);

        // A cancelled receipt cannot be invoiced.
        var product = await StockProductAsync(shop, 2, "Folie");
        var cancelled = (await shop.PostAsync("/api/pos/sales", Sale(register, product, 5_00), Key())).Expect(HttpStatusCode.Created).Json;
        (await shop.PostAsync($"/api/pos/sales/{Id(cancelled)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created);
        Assert.Equal("pos.invoice_receipt_refunded", (await shop.PostAsync($"/api/pos/sales/{Id(cancelled)}/invoice", new { customer })).Code);
    }

    [Fact]
    public async Task Invoices_are_immutable_and_the_series_cannot_have_gaps()
    {
        var (shop, _, receipt, _) = await SaleWithCustomerAsync();
        await SetVatIdAsync(shop);
        var invoice = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Expect(HttpStatusCode.Created).Json;

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<int>($"UPDATE pos.invoices SET number = 'RE-2000-000009' WHERE id = '{Id(invoice)}' RETURNING 1"));
        Assert.Equal("42501", update.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"DELETE FROM pos.invoices WHERE id = '{Id(invoice)}' RETURNING 1"));
        Assert.Equal("42501", delete.SqlState);

        // Taking a number without writing its invoice fails at commit (no gaps), as does skipping a number.
        var year = DateTime.UtcNow.Year;
        var gap = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            UPDATE pos.invoice_sequences SET next_number = next_number + 1 WHERE organization_id = '{shop.OrganizationId}' AND year = {year} RETURNING 1
            """));
        Assert.Equal("23514", gap.SqlState);
        var skip = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            UPDATE pos.invoice_sequences SET next_number = next_number + 2 WHERE organization_id = '{shop.OrganizationId}' AND year = {year} RETURNING 1
            """));
        Assert.Equal("23514", skip.SqlState);

        // The app role sees only its own organization's invoices.
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        await using var read = new NpgsqlCommand("SELECT count(*) FROM pos.invoices", app);
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
    }

    // ---- 6. cash rounding ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cash_rounding_register_rounds_cash_to_five_cents_on_receipt_and_z_report()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var product = await StockProductAsync(shop, 10, "Adapter");
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true, cashRounding = true })).Expect(HttpStatusCode.OK);
        Assert.True((await shop.GetAsync($"/api/pos/registers/{register}")).Json["cashRounding"]!.GetValue<bool>());
        // Omitting the field keeps the setting.
        (await shop.PutAsync($"/api/pos/registers/{register}", new { name = "Kasse 1", isActive = true })).Expect(HttpStatusCode.OK);
        Assert.True((await shop.GetAsync($"/api/pos/registers/{register}")).Json["cashRounding"]!.GetValue<bool>());
        var session = await OpenSessionAsync(shop, register, 50_00);

        // 12.33 € in cash: 12.35 € due, 20.00 € tendered → 7.65 € change, rounding +0.02 €.
        Assert.Equal("pos.payment_insufficient", (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 12_33, discountMinor = 0 } },
            payments = new[] { new { method = "Cash", amountMinor = 12_33 } },
        }, Key())).Code);
        var cash = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 12_33, discountMinor = 0 } },
            payments = new[] { new { method = "Cash", amountMinor = 20_00 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal((12_33L, 2L, 7_65L), (cash["totalGrossMinor"]!.GetValue<long>(), cash["roundingMinor"]!.GetValue<long>(), cash["changeMinor"]!.GetValue<long>()));
        Assert.Equal(12_35, cash["payments"]!.AsArray().Single()!["amountMinor"]!.GetValue<long>());
        Assert.Equal(2, cash["document"]!["roundingMinor"]!.GetValue<long>());

        // Card payments are exact; 12.32 € in cash rounds down.
        var card = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 12_33, discountMinor = 0 } },
            payments = new[] { new { method = "Card", amountMinor = 12_33 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(0, card["roundingMinor"]!.GetValue<long>());
        var down = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 12_32, discountMinor = 0 } },
            payments = new[] { new { method = "Cash", amountMinor = 12_30 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(-2, down["roundingMinor"]!.GetValue<long>());

        // Storno mirrors the rounding (and the cash paid).
        var storno = (await shop.PostAsync($"/api/pos/sales/{Id(cash)}/storno", new { reason = "Fehlbon" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(-2, storno["roundingMinor"]!.GetValue<long>());

        // Z-report: rounding total; expected cash = 50.00 + 12.35 + 12.30 − 12.35 = 62.30.
        var closed = (await shop.PostAsync($"/api/pos/sessions/{session}/close", new { countedCashMinor = 62_30 })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(-2, closed["summary"]!["roundingMinor"]!.GetValue<long>());
        Assert.Equal(62_30, closed["expectedCashMinor"]!.GetValue<long>());
        Assert.Equal(0, closed["differenceMinor"]!.GetValue<long>());

        // The database keeps the rounding within ±2 cents.
        var ck = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            SET session_replication_role = replica;
            UPDATE pos.receipts SET rounding_minor = 3 WHERE id = '{Id(down)}';
            SELECT 1;
            """));
        Assert.Equal("23514", ck.SqlState); // CHECK holds even with triggers off (session-local setting, own connection)
    }

    // ---- 1. product images on POS tiles ------------------------------------------------------------------------------------

    [Fact]
    public async Task Product_grid_carries_the_primary_image_thumbnail_from_the_catalog()
    {
        var shop = await PosShopAsync();
        var register = await RegisterAsync(shop);
        var withImages = await StockProductAsync(shop, 3, "Bild");
        var withoutImage = await StockProductAsync(shop, 3, "Ohne");
        var first = (await UploadImageAsync(shop, withImages, TestImages.Png(60, 60))).Expect(HttpStatusCode.Created).Json;
        var second = (await UploadImageAsync(shop, withImages, TestImages.Png(80, 40))).Expect(HttpStatusCode.Created).Json;

        async Task<JsonNode> TileAsync(Guid productId) =>
            (await shop.GetAsync($"/api/pos/registers/{register}/products?pageSize=100")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray()
            .Single(i => Id(i!, "productId") == productId)!;

        Assert.Equal(first["thumbnailUrl"]!.GetValue<string>(), (await TileAsync(withImages))["imageUrl"]!.GetValue<string>());
        Assert.Null((await TileAsync(withoutImage))["imageUrl"]);

        // Reordering makes the second image primary: the tile follows.
        (await shop.PutAsync($"/api/catalog/products/{withImages}/images/order", new { imageIds = new[] { Id(second), Id(first) } }))
            .Expect(HttpStatusCode.OK);
        var url = (await TileAsync(withImages))["imageUrl"]!.GetValue<string>();
        Assert.Equal($"/api/catalog/images/{Id(second)}/thumb", url);

        // The URL serves the thumbnail without authentication (public, immutable).
        var anonymous = fixture.CreateClient();
        var file = await anonymous.GetFileAsync(url);
        Assert.Equal(HttpStatusCode.OK, file.Status);
        Assert.Equal("image/webp", file.MediaType);
    }
}
