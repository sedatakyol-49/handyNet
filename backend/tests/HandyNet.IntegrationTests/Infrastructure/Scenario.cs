using System.Net;

namespace HandyNet.IntegrationTests.Infrastructure;

/// <summary>Builds the business setup most tests need: verified supplier and shop, with modules enabled.</summary>
public sealed class Scenario(HandyNetFixture fixture)
{
    private static int _counter;

    public static string UniqueSuffix() => $"{DateTime.UtcNow:HHmmss}{Interlocked.Increment(ref _counter)}{Random.Shared.Next(1000)}";

    /// <summary>Creates a valid EAN-13 that is unique for this test run.</summary>
    public static string NewEan()
    {
        var body = "40" + Random.Shared.NextInt64(0, 10_000_000_000).ToString("D10");
        var sum = body.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + (10 - sum % 10) % 10;
    }

    public async Task<ApiClient> AdminAsync()
    {
        var admin = fixture.CreateClient();
        await admin.LoginAsync(HandyNetFixture.AdminEmail, HandyNetFixture.AdminPassword);
        return admin;
    }

    /// <summary>Registers a business, logs in its owner and returns a client acting for it.</summary>
    public async Task<ApiClient> RegisterAsync(string kind, bool wholesaler, bool retailer = false)
    {
        var suffix = UniqueSuffix();
        var email = $"{kind}-{suffix}@test.de";
        const string password = "Sicher-Passwort-123";
        var client = fixture.CreateClient();
        var registration = (await client.PostAsync("/api/registration", new
        {
            email,
            displayName = kind,
            password,
            organization = new
            {
                name = $"{kind} {suffix}",
                legalName = $"{kind} {suffix} GmbH",
                street = "Hauptstraße 1",
                postalCode = "10115",
                city = "Berlin",
                isWholesaler = wholesaler,
                isRetailer = retailer || !wholesaler,
                isRepairProvider = false,
            },
        })).Expect(HttpStatusCode.Created);

        await client.LoginAsync(email, password);
        client.OrganizationId = Guid.Parse(registration.Json["organizationId"]!.GetValue<string>());
        return client;
    }

    public async Task ApproveAsync(ApiClient organization, params string[] modules)
    {
        var admin = await AdminAsync();
        (await admin.PostAsync($"/api/admin/organizations/{organization.OrganizationId}/verify")).Expect(HttpStatusCode.OK);
        (await admin.PutAsync($"/api/admin/organizations/{organization.OrganizationId}/modules", new { modules })).Expect(HttpStatusCode.OK);
    }

    public async Task<(ApiClient Supplier, ApiClient Shop)> SupplierAndShopAsync()
    {
        var supplier = await RegisterAsync("grosshandel", wholesaler: true);
        var shop = await RegisterAsync("handyshop", wholesaler: false);
        await ApproveAsync(supplier, "ProductImport", "Inventory", "B2BSales");
        await ApproveAsync(shop, "B2BPurchasing", "Inventory");
        return (supplier, shop);
    }

    /// <summary>Imports and commits one offer; returns its marketplace listing as seen by <paramref name="buyer"/>.</summary>
    public async Task<(Guid OfferId, string Ean)> PublishOfferAsync(ApiClient supplier, ApiClient buyer, int quantity, string price = "89,90")
    {
        var ean = NewEan();
        var sku = "SKU-" + UniqueSuffix();
        var csv = $"Artikelnummer;EAN;Marke;Bezeichnung;Kategorie;Qualität;Preis;Bestand\n{sku};{ean};Apple;iPhone 13 Display;Display;ServicePack;{price};{quantity}\n";
        var preview = (await supplier.UploadImportAsync("preise.csv", csv)).Expect(HttpStatusCode.Created);
        (await supplier.PostAsync($"/api/imports/{preview.Json["id"]}/commit")).Expect(HttpStatusCode.OK);

        // Searching by the supplier's own SKU must find the offer (as buyers often have the supplier's article number).
        var listings = (await buyer.GetAsync($"/api/marketplace/offers?search={sku}")).Expect(HttpStatusCode.OK);
        var offer = listings.Json["items"]!.AsArray().Single(i => i!["supplierSku"]!.GetValue<string>() == sku)!;
        return (Guid.Parse(offer["offerId"]!.GetValue<string>()), ean);
    }
}
