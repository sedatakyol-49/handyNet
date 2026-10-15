using System.Net;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class AccessControlTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    [Fact]
    public async Task Unverified_organization_cannot_use_modules()
    {
        var supplier = await _scenario.RegisterAsync("neu", wholesaler: true);
        var response = await supplier.GetAsync("/api/imports");
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("organization.not_verified", response.Code);
    }

    [Fact]
    public async Task Disabled_module_is_blocked_at_the_api_and_hidden_in_me()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var response = await shop.GetAsync("/api/imports");
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("module.disabled", response.Code);

        // Admin removes B2BSales: the supplier's offers endpoints are blocked immediately.
        (await supplier.GetAsync("/api/offers")).Expect(HttpStatusCode.OK);
        var admin = await _scenario.AdminAsync();
        (await admin.PutAsync($"/api/admin/organizations/{supplier.OrganizationId}/modules",
            new { modules = new[] { "ProductImport", "Inventory" } })).Expect(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync("/api/offers")).Status);

        var me = (await supplier.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        var modules = me.Json["memberships"]![0]!["modules"]!.AsArray().Select(m => m!.GetValue<string>()).ToList();
        Assert.DoesNotContain("B2BSales", modules);
    }

    [Fact]
    public async Task Organization_header_of_a_foreign_tenant_is_rejected()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var intruder = fixture.CreateClient();
        // Reuse the supplier's login but claim to act for the shop.
        typeof(ApiClient).GetProperty(nameof(ApiClient.Token))!.SetValue(intruder, supplier.Token);
        intruder.OrganizationId = shop.OrganizationId;

        var response = await intruder.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("tenant.missing", response.Code);
    }

    [Fact]
    public async Task B2B_offers_are_never_visible_anonymously()
    {
        var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/marketplace/offers")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/catalog/products")).Status);
    }

    [Fact]
    public async Task Only_platform_admins_reach_admin_endpoints()
    {
        var shop = await _scenario.RegisterAsync("shop", wholesaler: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/admin/organizations")).Status);
    }

    [Fact]
    public async Task Staff_role_cannot_commit_imports()
    {
        var (supplier, _) = await _scenario.SupplierAndShopAsync();
        var email = $"lager-{Scenario.UniqueSuffix()}@test.de";
        (await supplier.PostAsync("/api/organization/staff",
            new { email, displayName = "Lager", password = "Lager-Passwort-1", role = "Staff" })).Expect(HttpStatusCode.OK);

        var staff = fixture.CreateClient();
        await staff.LoginAsync(email, "Lager-Passwort-1");
        staff.OrganizationId = supplier.OrganizationId;

        var response = await staff.GetAsync("/api/imports");
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("permission.denied", response.Code);
    }

    [Fact]
    public async Task Existing_accounts_cannot_be_added_as_staff_without_consent()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var shopOwnerEmail = (await shop.GetAsync("/api/me")).Json["email"]!.GetValue<string>();

        var response = await supplier.PostAsync("/api/organization/staff",
            new { email = shopOwnerEmail, displayName = "X", password = "Irrelevant-123", role = "Staff" });
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("staff.invitation_required", response.Code);

        var memberships = (await shop.GetAsync("/api/me")).Json["memberships"]!.AsArray();
        Assert.Single(memberships);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var shop = await _scenario.RegisterAsync("lock", wholesaler: false);
        var me = (await shop.GetAsync("/api/me")).Json;
        var email = me["email"]!.GetValue<string>();
        var client = fixture.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/auth/login", new { email, password = "falsch" })).Status);

        var locked = await client.PostAsync("/api/auth/login", new { email, password = "Sicher-Passwort-123" });
        Assert.Equal(HttpStatusCode.Forbidden, locked.Status);
        Assert.Equal("auth.locked_out", locked.Code);
    }
}
