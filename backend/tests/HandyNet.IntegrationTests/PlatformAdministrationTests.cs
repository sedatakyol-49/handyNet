using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Migrations;
using HandyNet.Modules.Identity.Application;
using HandyNet.Modules.Notifications.Application;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Platform administration of companies (Unternehmen) and user accounts (Benutzer), ADR-0013: admin-only access, master data
/// with validation and audit, status history, memberships with the "keep an owner" rule, account locking with immediate
/// session end, last-admin protection, access log — on real PostgreSQL with the restricted app role.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PlatformAdministrationTests(HandyNetFixture fixture)
{
    private const string Password = "Sicher-Passwort-123";
    private readonly Scenario _scenario = new(fixture);

    private static Guid Id(JsonNode? node, string property = "id") => Guid.Parse(node![property]!.GetValue<string>());

    private static Guid UserId(ApiClient client) => Guid.Parse(System.Text.Encoding.UTF8.GetString(
        Convert.FromBase64String(Pad(client.Token!.Split('.')[1]))).Split("\"sub\":\"")[1][..36]);

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private async Task<(ApiClient Client, string Email, Guid UserId)> AddStaffAsync(ApiClient owner, string role = "Staff")
    {
        var email = $"staff-{Scenario.UniqueSuffix()}@test.de";
        var response = (await owner.PostAsync("/api/organization/staff", new { email, displayName = "Mitarbeiter " + role, password = Password, role }))
            .Expect(HttpStatusCode.OK);
        var client = fixture.CreateClient();
        await client.LoginAsync(email, Password);
        return (client, email, Id(response.Json, "userId"));
    }

    private async Task<T> AsOwnerAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> ConnectAsAppAsync(Guid? organizationId)
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection);
        set.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        await set.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task<Guid> AdminUserIdAsync() =>
        await AsOwnerAsync<Guid>("SELECT id FROM identity.users WHERE email = @e", ("e", HandyNetFixture.AdminEmail));

    private static object Profile(string name, string postalCode = "10115", string? vatId = null, string? website = null, string? taxNumber = null) => new
    {
        name,
        legalName = name + " GmbH",
        vatId,
        street = "Neue Straße 5",
        postalCode,
        city = "Hamburg",
        countryCode = "DE",
        isWholesaler = false,
        isRetailer = true,
        isRepairProvider = true,
        taxNumber,
        contactEmail = "Info@Example.de",
        contactPhone = "+49 40 123456",
        website,
    };

    // ------------------------------------------------------------------------------------------------ access

    [Fact]
    public async Task Admin_endpoints_refuse_tenants_and_anonymous_callers()
    {
        var shop = await _scenario.RegisterAsync("fremd", wholesaler: false);
        var org = shop.OrganizationId!.Value;
        var user = UserId(shop);
        var anonymous = fixture.CreateClient();

        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/admin/organizations", null),
            (HttpMethod.Get, $"/api/admin/organizations/{org}", null),
            (HttpMethod.Put, $"/api/admin/organizations/{org}", new { organization = Profile("Übernahme"), reason = "x" }),
            (HttpMethod.Post, $"/api/admin/organizations/{org}/reactivate", new { reason = "x" }),
            (HttpMethod.Put, $"/api/admin/organizations/{org}/members/{user}/role", new { role = "Manager" }),
            (HttpMethod.Post, $"/api/admin/organizations/{org}/members/{user}/remove", new { reason = "x" }),
            (HttpMethod.Post, $"/api/admin/organizations/{org}/transfer-ownership", new { userId = user }),
            (HttpMethod.Get, "/api/admin/users", null),
            (HttpMethod.Get, $"/api/admin/users/{user}", null),
            (HttpMethod.Post, $"/api/admin/users/{user}/disable", new { reason = "x" }),
            (HttpMethod.Post, $"/api/admin/users/{user}/grant-platform-admin", new { reason = "x" }),
            (HttpMethod.Post, $"/api/admin/users/{user}/password-reset", new { }),
            (HttpMethod.Put, $"/api/admin/users/{user}/display-name", new { displayName = "Hacker" }),
        };
        foreach (var (method, path, body) in calls)
        {
            // The owner — with and without the tenant header — is not a platform admin.
            Assert.Equal(HttpStatusCode.Forbidden, (await shop.SendAsync(method, path, body)).Status);
            var noHeader = fixture.CreateClient();
            typeof(ApiClient).GetProperty(nameof(ApiClient.Token))!.SetValue(noHeader, shop.Token);
            Assert.Equal(HttpStatusCode.Forbidden, (await noHeader.SendAsync(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(method, path, body)).Status);
        }

        // Nothing changed: the owner did not make itself admin and the name is untouched.
        var me = (await shop.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json;
        Assert.False(me["isPlatformAdmin"]!.GetValue<bool>());
        Assert.Equal("fremd", me["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Admin_calls_run_without_a_tenant_session()
    {
        // A platform admin who also owns a company: with that company's header the admin paths are refused (they must run
        // without a tenant, so RLS and the admin functions never mix with a tenant session).
        var admin = fixture.CreateClient();
        var email = $"admin-owner-{Scenario.UniqueSuffix()}@test.de";
        var registration = (await admin.PostAsync("/api/registration", new
        {
            email, displayName = "Admin mit Firma", password = Password,
            organization = new { name = "Adminfirma " + Scenario.UniqueSuffix(), legalName = "Adminfirma GmbH", street = "S 1", postalCode = "10115", city = "Berlin", isRetailer = true },
        })).Expect(HttpStatusCode.Created).Json;
        var userId = Id(registration, "userId");
        var org = Id(registration, "organizationId");
        (await (await _scenario.AdminAsync()).PostAsync($"/api/admin/users/{userId}/grant-platform-admin", new { reason = "Test" })).Expect(HttpStatusCode.OK);
        await admin.LoginAsync(email, Password);

        admin.OrganizationId = org;
        Assert.Equal("admin.tenant_header", (await admin.GetAsync($"/api/admin/organizations/{org}")).Code);
        Assert.Equal("admin.tenant_header", (await admin.GetAsync("/api/admin/organizations")).Code);
        Assert.Equal("admin.tenant_header", (await admin.GetAsync("/api/admin/users")).Code);
        Assert.Equal("admin.tenant_header", (await admin.PostAsync($"/api/admin/users/{userId}/revoke-sessions", new { })).Code);

        admin.OrganizationId = null;
        (await admin.GetAsync($"/api/admin/organizations/{org}")).Expect(HttpStatusCode.OK);
        Assert.Equal("user.self_admin_revoke", (await admin.PostAsync($"/api/admin/users/{userId}/revoke-platform-admin", new { reason = "Ende Test" })).Code);
        (await (await _scenario.AdminAsync()).PostAsync($"/api/admin/users/{userId}/revoke-platform-admin", new { reason = "Ende Test" }))
            .Expect(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------------------------------------ companies

    [Fact]
    public async Task Company_list_filters_and_counts()
    {
        var admin = await _scenario.AdminAsync();
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        await _scenario.PublishOfferAsync(supplier, shop, quantity: 2);
        await AddStaffAsync(supplier);
        var pos = await _scenario.RegisterAsync("kassenfirma", wholesaler: false);
        await _scenario.ApproveAsync(pos, "Pos", "Inventory");
        (await pos.PostAsync("/api/pos/registers", new { name = "Theke 1" })).Expect(HttpStatusCode.Created);
        (await pos.PostAsync("/api/pos/registers", new { name = "Theke 2" })).Expect(HttpStatusCode.Created);

        var supplierName = (await supplier.GetAsync("/api/me")).Json["memberships"]![0]!["organizationName"]!.GetValue<string>();
        var bySearch = (await admin.GetAsync($"/api/admin/organizations?search={Uri.EscapeDataString(supplierName)}")).Expect(HttpStatusCode.OK).Json;
        var row = bySearch["items"]!.AsArray().Single()!;
        Assert.Equal(2, row["memberCount"]!.GetValue<int>());
        Assert.Equal(1, row["offerCount"]!.GetValue<int>());
        Assert.Equal(0, row["registerCount"]!.GetValue<int>());

        var posName = (await pos.GetAsync("/api/me")).Json["memberships"]![0]!["organizationName"]!.GetValue<string>();
        var posRow = (await admin.GetAsync($"/api/admin/organizations?search={Uri.EscapeDataString(posName)}&module=Pos&capability=retailer"))
            .Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single()!;
        Assert.Equal(2, posRow["registerCount"]!.GetValue<int>());

        // Filters exclude: the POS company is no wholesaler, has no B2BSales, and was not registered tomorrow.
        Assert.Empty((await admin.GetAsync($"/api/admin/organizations?search={Uri.EscapeDataString(posName)}&capability=wholesaler")).Json["items"]!.AsArray());
        Assert.Empty((await admin.GetAsync($"/api/admin/organizations?search={Uri.EscapeDataString(posName)}&module=B2BSales")).Json["items"]!.AsArray());
        var tomorrow = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var yesterday = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        Assert.Empty((await admin.GetAsync($"/api/admin/organizations?search={Uri.EscapeDataString(posName)}&createdFrom={tomorrow}")).Json["items"]!.AsArray());
        Assert.Single((await admin.GetAsync($"/api/admin/organizations?search={Uri.EscapeDataString(posName)}&createdFrom={yesterday}&createdTo={tomorrow}")).Json["items"]!.AsArray());

        Assert.Equal("validation.failed", (await admin.GetAsync("/api/admin/organizations?module=Nope")).Code);
        Assert.Equal("validation.failed", (await admin.GetAsync("/api/admin/organizations?capability=bank")).Code);
        Assert.Equal("validation.failed", (await admin.GetAsync($"/api/admin/organizations?createdFrom={tomorrow}&createdTo={yesterday}")).Code);
    }

    [Fact]
    public async Task Company_detail_shows_master_data_history_members_and_registers_and_is_access_logged()
    {
        var admin = await _scenario.AdminAsync();
        var adminId = await AdminUserIdAsync();
        var shop = await _scenario.RegisterAsync("detailfirma", wholesaler: false);
        var org = shop.OrganizationId!.Value;
        await _scenario.ApproveAsync(shop, "Pos", "Inventory");
        (await shop.PostAsync("/api/pos/registers", new { name = "Hauptkasse" })).Expect(HttpStatusCode.Created);
        var (_, staffEmail, _) = await AddStaffAsync(shop);

        Assert.Equal("reason.required", (await admin.PostAsync($"/api/admin/organizations/{org}/suspend", new { reason = " " })).Code);
        (await admin.PostAsync($"/api/admin/organizations/{org}/suspend", new { reason = "Zahlungsverzug" })).Expect(HttpStatusCode.OK);
        Assert.Equal("organization.invalid_status", (await admin.PostAsync($"/api/admin/organizations/{org}/suspend", new { reason = "nochmal" })).Code);
        var reactivated = (await admin.PostAsync($"/api/admin/organizations/{org}/reactivate", new { reason = "bezahlt" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Verified", reactivated["status"]!.GetValue<string>());
        Assert.Equal("organization.invalid_status", (await admin.PostAsync($"/api/admin/organizations/{org}/reactivate", new { })).Code);

        var reads = await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Organization' AND entity_id = @id AND action = 'AdminRead'", ("id", org.ToString()));
        var detail = (await admin.GetAsync($"/api/admin/organizations/{org}")).Expect(HttpStatusCode.OK).Json;

        Assert.Equal(org, Id(detail["organization"]));
        Assert.Equal("Berlin", detail["organization"]!["city"]!.GetValue<string>());
        Assert.Equal(["Hauptkasse"], detail["registers"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(1, detail["organization"]!["registerCount"]!.GetValue<int>());
        Assert.True(detail["version"]!.GetValue<uint>() > 0);

        var history = detail["statusHistory"]!.AsArray().Select(h => (From: h!["from"]?.GetValue<string>(), To: h["to"]!.GetValue<string>(),
            Reason: h["reason"]?.GetValue<string>(), By: h["by"]?["id"]?.GetValue<string>())).ToList();
        Assert.Equal([(null, "PendingVerification"), ("PendingVerification", "Verified"), ("Verified", "Suspended"), ("Suspended", "Verified")],
            history.Select(h => (h.From, h.To)));
        Assert.Equal("Zahlungsverzug", history[2].Reason);
        Assert.Equal("bezahlt", history[3].Reason);
        Assert.Equal(adminId.ToString(), history[2].By);
        Assert.Equal(UserId(shop).ToString(), history[0].By); // the registering owner

        var members = detail["members"]!.AsArray();
        Assert.Equal(["Owner", "Staff"], members.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Contains(members, m => m!["email"]!.GetValue<string>() == staffEmail);

        // No secrets and no operational data in the payload.
        var text = detail.ToJsonString();
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", text, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(reads + 1, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Organization' AND entity_id = @id AND action = 'AdminRead' AND user_id = @admin",
            ("id", org.ToString()), ("admin", adminId)));
        Assert.Equal(1L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_id = @id AND action = 'AdminSuspend' AND changes ->> 'Reason' = 'Zahlungsverzug'",
            ("id", org.ToString())));

        Assert.Equal("organization.not_found", (await admin.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}")).Code);
    }

    [Fact]
    public async Task Master_data_is_validated_like_the_registration_and_audited_with_before_and_after()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await _scenario.RegisterAsync("stamm", wholesaler: false);
        var org = shop.OrganizationId!.Value;
        var version = (await admin.GetAsync($"/api/admin/organizations/{org}")).Json["version"]!.GetValue<uint>();
        var name = "Stammdaten " + Scenario.UniqueSuffix();

        // Validation (422, same rules as the registration plus the contact fields).
        foreach (var invalid in new[]
                 {
                     Profile(name, postalCode: "1234"),
                     Profile(name, vatId: "DE12"),
                     Profile(name, website: "www.ohne-schema.de"),
                     Profile(name, taxNumber: "12/34"),
                     Profile(""),
                 })
        {
            var response = await admin.PutAsync($"/api/admin/organizations/{org}", new { organization = invalid, version });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
            Assert.Equal("validation.failed", response.Code);
        }
        var noCapability = await admin.PutAsync($"/api/admin/organizations/{org}", new
        {
            organization = new { name, legalName = name, street = "x", postalCode = "10115", city = "x", isWholesaler = false, isRetailer = false, isRepairProvider = false },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCapability.Status);

        var vat = "DE" + Random.Shared.NextInt64(100_000_000, 999_999_999);
        var updated = (await admin.PutAsync($"/api/admin/organizations/{org}", new
        {
            organization = Profile(name, vatId: vat, website: "https://www.stamm.example.de", taxNumber: "21/815/08150"),
            reason = "Umfirmierung laut Handelsregister",
            version,
        })).Expect(HttpStatusCode.OK).Json["organization"]!;
        Assert.Equal(name, updated["name"]!.GetValue<string>());
        Assert.Equal("Hamburg", updated["city"]!.GetValue<string>());
        Assert.Equal("info@example.de", updated["contactEmail"]!.GetValue<string>());
        Assert.Equal("21/815/08150", updated["taxNumber"]!.GetValue<string>());
        Assert.False(updated["isWholesaler"]!.GetValue<bool>());

        // The tenant sees the new name; the stale form version is refused; an unchanged form is a conflict too.
        Assert.Equal(name, (await shop.GetAsync("/api/me")).Json["memberships"]![0]!["organizationName"]!.GetValue<string>());
        Assert.Equal("concurrency", (await admin.PutAsync($"/api/admin/organizations/{org}", new { organization = Profile(name + " B"), version })).Code);
        Assert.Equal("organization.unchanged", (await admin.PutAsync($"/api/admin/organizations/{org}", new
        {
            organization = Profile(name, vatId: vat, website: "https://www.stamm.example.de", taxNumber: "21/815/08150"),
        })).Code);

        // VAT ID of another company: 409.
        var other = await _scenario.RegisterAsync("andere", wholesaler: false);
        var conflict = await admin.PutAsync($"/api/admin/organizations/{other.OrganizationId}", new { organization = Profile("Andere " + Scenario.UniqueSuffix(), vatId: vat) });
        Assert.Equal("organization.vat_taken", conflict.Code);

        var audit = await AsOwnerAsync<string>(
            "SELECT changes::text FROM infra.audit_log WHERE entity_id = @id AND action = 'AdminUpdate' ORDER BY id DESC LIMIT 1", ("id", org.ToString()));
        var changes = JsonNode.Parse(audit)!;
        Assert.Equal("Umfirmierung laut Handelsregister", changes["Reason"]!.GetValue<string>());
        Assert.Equal("Berlin", changes["Before"]!["City"]!.GetValue<string>());
        Assert.Equal("Hamburg", changes["After"]!["City"]!.GetValue<string>());
        Assert.Equal("platform", changes["Source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Member_roles_change_but_an_organization_never_loses_its_last_owner()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await _scenario.RegisterAsync("rollen", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Inventory");
        var org = shop.OrganizationId!.Value;
        var ownerId = UserId(shop);
        var (staff, _, staffId) = await AddStaffAsync(shop);
        staff.OrganizationId = org;

        // Only owner cannot be demoted or removed.
        Assert.Equal("organization.last_owner", (await admin.PutAsync($"/api/admin/organizations/{org}/members/{ownerId}/role", new { role = "Manager" })).Code);
        Assert.Equal("organization.last_owner", (await admin.PostAsync($"/api/admin/organizations/{org}/members/{ownerId}/remove", new { reason = "Test" })).Code);
        Assert.Equal("reason.required", (await admin.PostAsync($"/api/admin/organizations/{org}/members/{staffId}/remove", new { })).Code);
        Assert.Equal("membership.not_found", (await admin.PutAsync($"/api/admin/organizations/{org}/members/{Guid.NewGuid()}/role", new { role = "Manager" })).Code);

        // Staff → Manager takes effect on the member's next request (organization.manage stays with the owner).
        var promoted = (await admin.PutAsync($"/api/admin/organizations/{org}/members/{staffId}/role", new { role = "Manager", reason = "Filialleitung" }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Manager", promoted["members"]!.AsArray().Single(m => Id(m, "userId") == staffId)!["role"]!.GetValue<string>());
        var permissions = (await staff.GetAsync("/api/me")).Json["memberships"]![0]!["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Contains("inventory.manage", permissions);
        Assert.DoesNotContain("organization.manage", permissions);

        // Transfer: the manager becomes the only owner, the previous owner a manager — one transaction.
        var transferred = (await admin.PostAsync($"/api/admin/organizations/{org}/transfer-ownership", new { userId = staffId, reason = "Inhaberwechsel" }))
            .Expect(HttpStatusCode.OK).Json["members"]!.AsArray();
        Assert.Equal("Owner", transferred.Single(m => Id(m, "userId") == staffId)!["role"]!.GetValue<string>());
        Assert.Equal("Manager", transferred.Single(m => Id(m, "userId") == ownerId)!["role"]!.GetValue<string>());
        Assert.Equal("membership.already_owner", (await admin.PostAsync($"/api/admin/organizations/{org}/transfer-ownership", new { userId = staffId })).Code);
        (await staff.GetAsync("/api/organization/staff")).Expect(HttpStatusCode.OK); // new owner manages the team

        // The previous owner can now be removed; the account stays, without organization.
        (await admin.PostAsync($"/api/admin/organizations/{org}/members/{ownerId}/remove", new { reason = "Ausgeschieden" })).Expect(HttpStatusCode.OK);
        Assert.Empty((await shop.GetAsync("/api/me")).Json["memberships"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/inventory/warehouses")).Status);
        Assert.Equal("membership.not_found", (await admin.PostAsync($"/api/admin/organizations/{org}/members/{ownerId}/remove", new { reason = "nochmal" })).Code);

        var me = (await admin.GetAsync($"/api/admin/users/{ownerId}")).Json;
        var ownerEmail = me["email"]!.GetValue<string>();
        var filtered = (await admin.GetAsync($"/api/admin/users?noOrganization=true&search={Uri.EscapeDataString(ownerEmail)}")).Json["items"]!.AsArray();
        Assert.Single(filtered);
        Assert.Empty((await admin.GetAsync($"/api/admin/users?noOrganization=false&search={Uri.EscapeDataString(ownerEmail)}")).Json["items"]!.AsArray());

        Assert.Equal(1L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Membership' AND action = 'AdminRemoveMember' AND entity_id = @id AND changes ->> 'Reason' = 'Ausgeschieden'",
            ("id", $"{org},{ownerId}")));
        Assert.Equal(1L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Membership' AND action = 'AdminTransferOwner' AND entity_id = @id AND changes ->> 'Reason' = 'Inhaberwechsel'",
            ("id", $"{org},{staffId}")));
    }

    [Fact]
    public async Task Database_enforces_owner_rule_membership_deletion_path_and_admin_functions()
    {
        var shop = await _scenario.RegisterAsync("dbregel", wholesaler: false);
        var org = shop.OrganizationId!.Value;
        var ownerId = UserId(shop);
        var adminId = await AdminUserIdAsync();

        await using (var app = await ConnectAsAppAsync(null))
        {
            // Demoting the last owner fails at commit (deferred constraint trigger), even without application code.
            await using var tx = await app.BeginTransactionAsync();
            await using (var demote = new NpgsqlCommand($"UPDATE tenancy.memberships SET role = 'Manager' WHERE organization_id = '{org}'", app, tx))
                Assert.Equal(1, await demote.ExecuteNonQueryAsync());
            var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal("ck_memberships_owner_required", ex.ConstraintName);

            // The app role cannot delete memberships directly.
            await using var delete = new NpgsqlCommand($"DELETE FROM tenancy.memberships WHERE organization_id = '{org}'", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);

            // The removal function checks that the caller passes an active platform admin, and keeps the owner rule.
            await using var notAdmin = new NpgsqlCommand($"SELECT tenancy.admin_remove_membership('{org}', '{ownerId}', '{ownerId}', 'x', now())", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => notAdmin.ExecuteScalarAsync())).SqlState);
            await using var lastOwner = new NpgsqlCommand($"SELECT tenancy.admin_remove_membership('{org}', '{ownerId}', '{adminId}', 'x', now())", app);
            Assert.Equal("ck_memberships_owner_required", (await Assert.ThrowsAsync<PostgresException>(() => lastOwner.ExecuteScalarAsync())).ConstraintName);

            // History is append-only.
            await using var rewrite = new NpgsqlCommand("UPDATE tenancy.organization_status_changes SET reason = 'x'", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => rewrite.ExecuteNonQueryAsync())).SqlState);
            await using var erase = new NpgsqlCommand("DELETE FROM tenancy.organization_status_changes", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => erase.ExecuteNonQueryAsync())).SqlState);
        }

        // Admin functions refuse to run for a tenant session.
        await using (var tenantSession = await ConnectAsAppAsync(org))
        {
            foreach (var sql in new[]
                     {
                         $"SELECT tenancy.admin_remove_membership('{org}', '{ownerId}', '{adminId}', 'x', now())",
                         $"SELECT count(*) FROM pos.admin_register_names(ARRAY['{org}']::uuid[])",
                         $"SELECT count(*) FROM marketplace.admin_offer_counts(ARRAY['{org}']::uuid[])",
                     })
            {
                await using var command = new NpgsqlCommand(sql, tenantSession);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
            }
        }

        // At least one active platform admin remains (trigger, also for direct SQL).
        await using (var app = await ConnectAsAppAsync(null))
        {
            await using var tx = await app.BeginTransactionAsync();
            await using (var demoteAll = new NpgsqlCommand("UPDATE identity.users SET is_platform_admin = false WHERE is_platform_admin", app, tx))
                await demoteAll.ExecuteNonQueryAsync();
            var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal("ck_users_last_platform_admin", ex.ConstraintName);
        }
        Assert.True(await AsOwnerAsync<bool>("SELECT is_platform_admin FROM identity.users WHERE id = @id", ("id", adminId)));
    }

    // ------------------------------------------------------------------------------------------------ users

    [Fact]
    public async Task User_list_search_filters_and_detail_without_secrets()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await _scenario.RegisterAsync("benutzerliste", wholesaler: false);
        var userId = UserId(shop);
        var detail = (await admin.GetAsync($"/api/admin/users/{userId}")).Expect(HttpStatusCode.OK).Json;
        var email = detail["email"]!.GetValue<string>();

        Assert.Equal("benutzerliste", detail["displayName"]!.GetValue<string>());
        Assert.False(detail["emailVerified"]!.GetValue<bool>());
        Assert.False(detail["isDisabled"]!.GetValue<bool>());
        Assert.Equal(1, detail["activeSessions"]!.GetValue<int>());
        Assert.NotNull(detail["lastLoginAt"]);
        var membership = detail["memberships"]!.AsArray().Single()!;
        Assert.Equal(shop.OrganizationId, Id(membership, "organizationId"));
        Assert.Equal("Owner", membership["role"]!.GetValue<string>());
        var text = detail.ToJsonString();
        Assert.DoesNotContain("password\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);

        var list = (await admin.GetAsync($"/api/admin/users?search={Uri.EscapeDataString(email.ToUpperInvariant())}")).Expect(HttpStatusCode.OK).Json;
        var item = list["items"]!.AsArray().Single()!;
        Assert.Equal(userId, Id(item));
        Assert.Single(item["memberships"]!.AsArray());
        Assert.Single((await admin.GetAsync($"/api/admin/users?search={Uri.EscapeDataString(email)}&emailVerified=false&disabled=false&locked=false&platformAdmin=false")).Json["items"]!.AsArray());
        Assert.Empty((await admin.GetAsync($"/api/admin/users?search={Uri.EscapeDataString(email)}&emailVerified=true")).Json["items"]!.AsArray());
        Assert.Empty((await admin.GetAsync($"/api/admin/users?search={Uri.EscapeDataString(email)}&platformAdmin=true")).Json["items"]!.AsArray());
        Assert.Contains((await admin.GetAsync($"/api/admin/users?platformAdmin=true&pageSize=200")).Json["items"]!.AsArray(),
            u => u!["email"]!.GetValue<string>() == HandyNetFixture.AdminEmail);

        Assert.Equal("user.not_found", (await admin.GetAsync($"/api/admin/users/{Guid.NewGuid()}")).Code);
        Assert.Equal(1L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_type = 'User' AND entity_id = @id AND action = 'AdminRead'", ("id", userId.ToString())));
    }

    [Fact]
    public async Task Disabled_user_cannot_sign_in_and_all_sessions_end_immediately_until_enabled()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await _scenario.RegisterAsync("gesperrt", wholesaler: false);
        var userId = UserId(shop);
        var email = (await admin.GetAsync($"/api/admin/users/{userId}")).Json["email"]!.GetValue<string>();
        var device = fixture.CreateClient();
        var login = (await device.PostAsync("/api/auth/login", new { email, password = Password })).Expect(HttpStatusCode.OK).Json;
        var refreshToken = login["refreshToken"]!.GetValue<string>();
        (await shop.GetAsync("/api/me")).Expect(HttpStatusCode.OK);

        Assert.Equal("reason.required", (await admin.PostAsync($"/api/admin/users/{userId}/disable", new { })).Code);
        var disabled = (await admin.PostAsync($"/api/admin/users/{userId}/disable", new { reason = "Missbrauchsverdacht" })).Expect(HttpStatusCode.OK).Json;
        Assert.True(disabled["isDisabled"]!.GetValue<bool>());
        Assert.Equal("Missbrauchsverdacht", disabled["disabledReason"]!.GetValue<string>());
        Assert.Equal(0, disabled["activeSessions"]!.GetValue<int>());
        Assert.Equal("user.already_disabled", (await admin.PostAsync($"/api/admin/users/{userId}/disable", new { reason = "x" })).Code);

        // Existing access token, refresh token and new sign-ins: all refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await shop.GetAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsync("/api/auth/refresh", new { refreshToken })).Status);
        var refused = await fixture.CreateClient().PostAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal("auth.invalid_credentials", refused.Code);
        Assert.Equal("RevokedByAdmin", await AsOwnerAsync<string>(
            "SELECT CASE WHEN bool_and(revoked_at IS NOT NULL OR used_at IS NOT NULL) THEN 'RevokedByAdmin' ELSE 'live' END FROM identity.refresh_tokens WHERE user_id = @u",
            ("u", userId)));
        Assert.Equal(2L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM identity.refresh_tokens WHERE user_id = @u AND revocation_reason = 'AccountDisabled'", ("u", userId)));
        Assert.Single((await admin.GetAsync($"/api/admin/users?disabled=true&search={Uri.EscapeDataString(email)}")).Json["items"]!.AsArray());

        (await admin.PostAsync($"/api/admin/users/{userId}/enable", new { reason = "geklärt" })).Expect(HttpStatusCode.OK);
        var again = fixture.CreateClient();
        await again.LoginAsync(email, Password);
        (await again.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        Assert.Equal("user.not_disabled", (await admin.PostAsync($"/api/admin/users/{userId}/enable", new { })).Code);

        Assert.Equal(1L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_type = 'User' AND entity_id = @id AND action = 'AdminDisable' AND changes ->> 'Reason' = 'Missbrauchsverdacht' AND user_id = @admin",
            ("id", userId.ToString()), ("admin", await AdminUserIdAsync())));
    }

    [Fact]
    public async Task Lockout_can_be_cleared_and_sessions_revoked()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await _scenario.RegisterAsync("sperre", wholesaler: false);
        var userId = UserId(shop);
        var email = (await admin.GetAsync($"/api/admin/users/{userId}")).Json["email"]!.GetValue<string>();
        var attacker = fixture.CreateClient();
        for (var i = 0; i < 5; i++)
            await attacker.PostAsync("/api/auth/login", new { email, password = "falsch-falsch-1" });
        Assert.Equal("auth.locked_out", (await attacker.PostAsync("/api/auth/login", new { email, password = Password })).Code);
        var locked = (await admin.GetAsync($"/api/admin/users/{userId}")).Json;
        Assert.True(locked["isLockedOut"]!.GetValue<bool>());
        Assert.Single((await admin.GetAsync($"/api/admin/users?locked=true&search={Uri.EscapeDataString(email)}")).Json["items"]!.AsArray());

        (await admin.PostAsync($"/api/admin/users/{userId}/clear-lockout", new { reason = "Kunde hat angerufen" })).Expect(HttpStatusCode.OK);
        Assert.Equal("user.not_locked_out", (await admin.PostAsync($"/api/admin/users/{userId}/clear-lockout", new { })).Code);
        var device = fixture.CreateClient();
        var login = (await device.PostAsync("/api/auth/login", new { email, password = Password })).Expect(HttpStatusCode.OK).Json;

        Assert.Equal(2, (await admin.GetAsync($"/api/admin/users/{userId}")).Json["activeSessions"]!.GetValue<int>());
        var revoked = (await admin.PostAsync($"/api/admin/users/{userId}/revoke-sessions", new { reason = "Gerät verloren" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(0, revoked["activeSessions"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await device.PostAsync("/api/auth/refresh", new { refreshToken = login["refreshToken"]!.GetValue<string>() })).Status);
    }

    [Fact]
    public async Task Admin_sends_account_emails_marks_verified_and_renames()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await _scenario.RegisterAsync("postfach", wholesaler: false);
        var userId = UserId(shop);
        var email = (await admin.GetAsync($"/api/admin/users/{userId}")).Json["email"]!.GetValue<string>();

        (await admin.PostAsync($"/api/admin/users/{userId}/password-reset", new { reason = "Kunde bittet um Link", language = "de" })).Expect(HttpStatusCode.OK);
        Assert.Equal("auth.reset_recently_sent", (await admin.PostAsync($"/api/admin/users/{userId}/password-reset", new { })).Code);
        Assert.Equal("validation.failed", (await admin.PostAsync($"/api/admin/users/{userId}/password-reset", new { language = "fr" })).Code);
        await ProcessUntilAsync(() => fixture.Emails.Sent.Any(e => e.To == email && e.Body.Contains("passwort-zuruecksetzen#token=")));

        // Registration already sent a verification link a moment ago: the cooldown applies to the admin as well.
        Assert.Equal("auth.verification_recently_sent", (await admin.PostAsync($"/api/admin/users/{userId}/resend-verification", new { })).Code);
        Assert.Equal("reason.required", (await admin.PostAsync($"/api/admin/users/{userId}/mark-email-verified", new { reason = "" })).Code);
        var verified = (await admin.PostAsync($"/api/admin/users/{userId}/mark-email-verified", new { reason = "Telefonisch bestätigt" })).Expect(HttpStatusCode.OK).Json;
        Assert.True(verified["emailVerified"]!.GetValue<bool>());
        Assert.True((await shop.GetAsync("/api/me")).Json["emailVerified"]!.GetValue<bool>());
        Assert.Equal("auth.email_already_verified", (await admin.PostAsync($"/api/admin/users/{userId}/resend-verification", new { })).Code);

        Assert.Equal("validation.failed", (await admin.PutAsync($"/api/admin/users/{userId}/display-name", new { displayName = " " })).Code);
        var renamed = (await admin.PutAsync($"/api/admin/users/{userId}/display-name", new { displayName = "Neuer Name", reason = "Tippfehler" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Neuer Name", renamed["displayName"]!.GetValue<string>());
        var audit = JsonNode.Parse(await AsOwnerAsync<string>(
            "SELECT changes::text FROM infra.audit_log WHERE entity_id = @id AND action = 'AdminDisplayName'", ("id", userId.ToString())))!;
        Assert.Equal("postfach", audit["Before"]!["DisplayName"]!.GetValue<string>());
        Assert.Equal("Neuer Name", audit["After"]!["DisplayName"]!.GetValue<string>());
        Assert.Equal(1L, await AsOwnerAsync<long>(
            "SELECT count(*) FROM infra.audit_log WHERE entity_id = @id AND action = 'AdminMarkVerified' AND changes ->> 'Reason' = 'Telefonisch bestätigt'",
            ("id", userId.ToString())));
    }

    [Fact]
    public async Task Platform_admin_role_is_granted_and_revoked_with_self_and_last_admin_protection()
    {
        var admin = await _scenario.AdminAsync();
        var adminId = await AdminUserIdAsync();
        var shop = await _scenario.RegisterAsync("zweitadmin", wholesaler: false);
        var userId = UserId(shop);
        var email = (await admin.GetAsync($"/api/admin/users/{userId}")).Json["email"]!.GetValue<string>();

        Assert.Equal("user.self_admin_revoke", (await admin.PostAsync($"/api/admin/users/{adminId}/revoke-platform-admin", new { reason = "x" })).Code);
        Assert.Equal("user.self_disable", (await admin.PostAsync($"/api/admin/users/{adminId}/disable", new { reason = "x" })).Code);
        Assert.True((await admin.GetAsync($"/api/admin/users/{adminId}")).Json["isSelf"]!.GetValue<bool>());

        (await admin.PostAsync($"/api/admin/users/{userId}/grant-platform-admin", new { reason = "Support-Team" })).Expect(HttpStatusCode.OK);
        Assert.Equal("user.already_platform_admin", (await admin.PostAsync($"/api/admin/users/{userId}/grant-platform-admin", new { })).Code);
        var second = fixture.CreateClient();
        await second.LoginAsync(email, Password);
        (await second.GetAsync("/api/admin/users?pageSize=1")).Expect(HttpStatusCode.OK);

        Assert.Equal("reason.required", (await admin.PostAsync($"/api/admin/users/{userId}/revoke-platform-admin", new { })).Code);
        (await admin.PostAsync($"/api/admin/users/{userId}/revoke-platform-admin", new { reason = "Team verlassen" })).Expect(HttpStatusCode.OK);
        // The second admin's token still carries the admin claim, but it is rejected at once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/admin/users?pageSize=1")).Status);
        var relogin = fixture.CreateClient();
        await relogin.LoginAsync(email, Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await relogin.GetAsync("/api/admin/users?pageSize=1")).Status);
        (await relogin.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        Assert.Equal("user.not_platform_admin", (await admin.PostAsync($"/api/admin/users/{userId}/revoke-platform-admin", new { reason = "x" })).Code);
    }

    private async Task ProcessUntilAsync(Func<bool> done)
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

    // ------------------------------------------------------------------------------------------------ migration backfill

    /// <summary>
    /// The migration fills the status history of existing organizations from the audit log (enum values stored as numbers),
    /// on a database of its own that is migrated to the previous migration first.
    /// </summary>
    [Fact]
    public async Task Migration_backfills_the_status_history_from_the_audit_log()
    {
        var database = $"handynet_test_backfill_{Guid.NewGuid():N}"[..40];
        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            // Generous timeouts: CREATE DATABASE and the full migration run slow down under parallel test load.
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin) { CommandTimeout = 180 };
            await create.ExecuteNonQueryAsync();
        }
        var connection = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database, CommandTimeout = 180 }.ConnectionString;
        try
        {
            await using (var db = DatabaseMigrator.CreateContext(connection))
                await db.GetService<IMigrator>().MigrateAsync("20261018181542_PosStaffAndDocuments");

            var owner = Guid.NewGuid();
            var adminId = Guid.NewGuid();
            var org = Guid.NewGuid();
            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await using var seed = new NpgsqlCommand($$$"""
                    INSERT INTO identity.users (id, email, display_name, password_hash, is_platform_admin, is_active, failed_login_count, created_at)
                    VALUES ('{{{owner}}}', 'o@x.de', 'O', 'h', false, true, 0, '2026-10-01T08:00:00Z'),
                           ('{{{adminId}}}', 'a@x.de', 'A', 'h', true, true, 0, '2026-10-01T08:00:00Z');
                    INSERT INTO tenancy.organizations (id, name, legal_name, street, postal_code, city, country_code, is_wholesaler, is_retailer,
                                                       is_repair_provider, status, status_reason, created_at, created_by, verified_at, verified_by)
                    VALUES ('{{{org}}}', 'Alt', 'Alt GmbH', 'S 1', '10115', 'Berlin', 'DE', false, true, false, 'Suspended', 'Zahlungsverzug',
                            '2026-10-01T09:00:00Z', '{{{owner}}}', '2026-10-02T09:00:00Z', '{{{adminId}}}');
                    INSERT INTO infra.audit_log (occurred_at, user_id, organization_id, entity_type, entity_id, action, changes) VALUES
                      ('2026-10-01T09:00:00Z', NULL, NULL, 'Organization', '{{{org}}}', 'Added', '{"Name":"Alt","Status":0}'),
                      ('2026-10-02T09:00:00Z', '{{{adminId}}}', NULL, 'Organization', '{{{org}}}', 'Modified', '{"Status":{"from":0,"to":1},"VerifiedBy":{"from":null,"to":"{{{adminId}}}"}}'),
                      ('2026-10-03T09:00:00Z', '{{{adminId}}}', NULL, 'Organization', '{{{org}}}', 'Modified', '{"Status":{"from":1,"to":3},"StatusReason":{"from":null,"to":"Zahlungsverzug"}}'),
                      ('2026-10-03T10:00:00Z', '{{{adminId}}}', NULL, 'Organization', '{{{org}}}', 'Modified', '{"Name":{"from":"Alt","to":"Alt"}}');
                    """, c);
                await seed.ExecuteNonQueryAsync();
            }

            await DatabaseMigrator.MigrateAsync(connection);

            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await using var read = new NpgsqlCommand(
                    $"SELECT coalesce(from_status, '-'), to_status, coalesce(reason, '-'), coalesce(changed_by::text, '-') FROM tenancy.organization_status_changes WHERE organization_id = '{org}' ORDER BY changed_at",
                    c);
                await using var reader = await read.ExecuteReaderAsync();
                var rows = new List<string>();
                while (await reader.ReadAsync())
                    rows.Add($"{reader.GetString(0)}>{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}");
                Assert.Equal(
                [
                    $"->PendingVerification|-|{owner}",
                    $"PendingVerification>Verified|-|{adminId}",
                    $"Verified>Suspended|Zahlungsverzug|{adminId}",
                ], rows);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
