using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Tests.Support;
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
