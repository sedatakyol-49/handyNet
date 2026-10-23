using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Tests.Support;
using Npgsql;
using SkiaSharp;

namespace HandyNet.IntegrationTests;

/// <summary>
/// The tenant's own company profile and logo (ADR-0014) on real PostgreSQL with RLS: own data only, owners write, staff
/// read, IBAN/VAT validation, concurrency, audit + admin-visible legal changes, logo processing and serving, logo and
/// letterhead on POS invoices and the public e-receipt, and the database policies behind it.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class CompanyProfileTests(HandyNetFixture fixture)
{
    private const string StaffPassword = "Mitarbeiter-Passwort-1";
    private const string ValidIban = "DE89 3704 0044 0532 0130 00";

    private readonly Scenario _scenario = new(fixture);

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    /// <summary>The GET body as an update request with changes applied (keeps every other field as loaded).</summary>
    private static JsonObject Edit(JsonNode profile, Action<JsonObject> change)
    {
        var request = new JsonObject();
        foreach (var name in new[]
                 {
                     "name", "legalName", "street", "postalCode", "city", "countryCode", "vatId", "taxNumber", "contactEmail", "contactPhone",
                     "website", "receiptHeader", "receiptFooter", "bankName", "iban", "bic", "managingDirector", "registerCourt", "registerNumber",
                     "paymentTermsDays", "version",
                 })
            request[name] = profile[name]?.DeepClone();
        change(request);
        return request;
    }

    private async Task<(ApiClient Client, Guid UserId)> MemberAsync(ApiClient owner, string role)
    {
        var email = $"mitglied-{Scenario.UniqueSuffix()}@test.de";
        var userId = Id((await owner.PostAsync("/api/organization/staff", new { email, displayName = role, password = StaffPassword, role }))
            .Expect(HttpStatusCode.OK).Json, "userId");
        var client = fixture.CreateClient();
        await client.LoginAsync(email, StaffPassword);
        client.OrganizationId = owner.OrganizationId;
        return (client, userId);
    }

    private static async Task<ApiResponse> UploadLogoAsync(ApiClient client, byte[] data, string contentType = "image/png", string name = "logo.png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        return await client.SendAsync(HttpMethod.Post, "/api/organization/logo", content: form);
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(HttpStatusCode Status, string? ContentType, byte[] Body, HttpResponseMessage Response)> RawGetAsync(HttpClient http, string path,
        string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        var response = await http.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync(), response);
    }

    [Fact]
    public async Task Owner_reads_and_changes_only_the_own_company_with_concurrency_and_audit()
    {
        var shop = await _scenario.RegisterAsync("firmendaten", wholesaler: false);
        var other = await _scenario.RegisterAsync("fremdfirma", wholesaler: false);

        var profile = (await shop.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(shop.OrganizationId, Id(profile));
        Assert.True(profile["canEdit"]!.GetValue<bool>());
        Assert.Null(profile["logo"]);

        var updated = (await shop.PutAsync("/api/organization/profile", Edit(profile, r =>
        {
            r["name"] = "Firmendaten Mitte";
            r["contactPhone"] = "+49 30 1234567";
            r["receiptHeader"] = "Ihr Handy-Profi in Berlin";
            r["receiptFooter"] = "Mo–Sa 10–19 Uhr\nUmtausch innerhalb von 14 Tagen mit Beleg";
            r["iban"] = ValidIban;
            r["bic"] = "COBADEFFXXX";
            r["bankName"] = "Commerzbank";
            r["managingDirector"] = "Erika Muster";
            r["registerCourt"] = "Amtsgericht Charlottenburg";
            r["registerNumber"] = "HRB 123456";
            r["paymentTermsDays"] = 14;
        }))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Firmendaten Mitte", updated["name"]!.GetValue<string>());
        Assert.Equal("DE89370400440532013000", updated["iban"]!.GetValue<string>());
        Assert.Equal(14, updated["paymentTermsDays"]!.GetValue<int>());
        Assert.NotEqual(profile["version"]!.GetValue<uint>(), updated["version"]!.GetValue<uint>());

        // The other company is untouched and sees only itself.
        var otherProfile = (await other.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(other.OrganizationId, Id(otherProfile));
        Assert.Null(otherProfile["iban"]);
        Assert.NotEqual("Firmendaten Mitte", otherProfile["name"]!.GetValue<string>());
        // The header of a foreign organization grants nothing (not a member).
        other.OrganizationId = shop.OrganizationId;
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/organization/profile")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync("/api/organization/profile", Edit(updated, r => r["name"] = "Übernommen"))).Status);

        // Stale version → 409; the change of the other writer stays.
        var stale = await shop.PutAsync("/api/organization/profile", Edit(profile, r => r["name"] = "Veraltet"));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("concurrency", stale.Code);
        // Version is required.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PutAsync("/api/organization/profile", Edit(updated, r => r.Remove("version")))).Status);

        // Audited with before/after (explicit tenant entry) next to the automatic entity entry.
        var audit = await AsOwnerAsync<string>($"""
            SELECT changes::text FROM infra.audit_log
             WHERE entity_type = 'Organization' AND entity_id = '{shop.OrganizationId}' AND action = 'TenantProfileUpdate'
             ORDER BY id DESC LIMIT 1
            """);
        var changes = JsonNode.Parse(audit)!;
        Assert.Equal("tenant", changes["Source"]!.GetValue<string>());
        Assert.Equal("Firmendaten Mitte", changes["After"]!["Name"]!.GetValue<string>());
        Assert.NotEqual("Firmendaten Mitte", changes["Before"]!["Name"]!.GetValue<string>());
        Assert.Null(changes["Before"]!["Iban"]);
        Assert.Equal("DE89370400440532013000", changes["After"]!["Iban"]!.GetValue<string>());
    }

    [Fact]
    public async Task Members_without_organization_manage_read_but_cannot_change_anything()
    {
        var shop = await _scenario.RegisterAsync("firmenteam", wholesaler: false);
        foreach (var role in new[] { "Staff", "Manager" })
        {
            var (member, _) = await MemberAsync(shop, role);
            var profile = (await member.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
            Assert.Equal(shop.OrganizationId, Id(profile));
            Assert.False(profile["canEdit"]!.GetValue<bool>());

            var put = await member.PutAsync("/api/organization/profile", Edit(profile, r => r["name"] = "Von " + role));
            Assert.Equal(HttpStatusCode.Forbidden, put.Status);
            Assert.Equal("permission.denied", put.Code);
            Assert.Equal("permission.denied", (await UploadLogoAsync(member, TestImages.Png(100, 50))).Code);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.SendAsync(HttpMethod.Delete, "/api/organization/logo")).Status);
        }
        var unchanged = (await shop.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        Assert.DoesNotContain("Von ", unchanged["name"]!.GetValue<string>());

        // Without a tenant header there is no profile at all.
        var noTenant = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noTenant.GetAsync("/api/organization/profile")).Status);
    }

    [Fact]
    public async Task Iban_bic_vat_and_text_lengths_are_validated()
    {
        var shop = await _scenario.RegisterAsync("firmencheck", wholesaler: false);
        var profile = (await shop.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;

        async Task<string[]> ErrorsFor(Action<JsonObject> change)
        {
            var response = await shop.PutAsync("/api/organization/profile", Edit(profile, change));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
            Assert.Equal("validation.failed", response.Code);
            return response.Json["errors"]!.AsObject().Select(e => e.Key).ToArray();
        }

        Assert.Contains("Iban", await ErrorsFor(r => r["iban"] = "DE89 3704 0044 0532 0130 01"));   // wrong check digits
        Assert.Contains("Iban", await ErrorsFor(r => r["iban"] = "DE89 3704 0044 0532 0130"));      // too short
        Assert.Contains("Iban", await ErrorsFor(r => r["bic"] = "COBADEFFXXX"));                     // BIC without IBAN
        Assert.Contains("Bic", await ErrorsFor(r => { r["iban"] = ValidIban; r["bic"] = "COBA"; }));
        Assert.Contains("VatId", await ErrorsFor(r => r["vatId"] = "DE12345"));
        Assert.Contains("VatId", await ErrorsFor(r => r["vatId"] = "ATU12345678"));                  // country DE
        Assert.Contains("PaymentTermsDays", await ErrorsFor(r => r["paymentTermsDays"] = 400));
        Assert.Contains("ReceiptFooter", await ErrorsFor(r => r["receiptFooter"] = new string('x', 1001)));
        Assert.Contains("ReceiptHeader", await ErrorsFor(r => r["receiptHeader"] = string.Join('\n', Enumerable.Repeat("Zeile", 13))));
        Assert.Contains("Website", await ErrorsFor(r => r["website"] = "www.ohne-schema.de"));
        Assert.Contains("LegalName", await ErrorsFor(r => r["legalName"] = ""));

        // A VAT ID of another company is refused.
        var other = await _scenario.RegisterAsync("firmenvat", wholesaler: false);
        var vat = $"DE{Random.Shared.Next(100_000_000, 999_999_999)}";
        var otherProfile = (await other.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        (await other.PutAsync("/api/organization/profile", Edit(otherProfile, r => r["vatId"] = vat))).Expect(HttpStatusCode.OK);
        Assert.Equal("organization.vat_taken", (await shop.PutAsync("/api/organization/profile", Edit(profile, r => r["vatId"] = vat))).Code);
    }

    [Fact]
    public async Task A_legal_name_or_vat_change_by_the_company_shows_up_in_the_admin_company_detail()
    {
        var shop = await _scenario.RegisterAsync("firmenrecht", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Pos");
        var profile = (await shop.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        var newLegalName = "Neue Rechtsform " + Scenario.UniqueSuffix() + " UG";
        (await shop.PutAsync("/api/organization/profile", Edit(profile, r => r["legalName"] = newLegalName))).Expect(HttpStatusCode.OK);

        var admin = await _scenario.AdminAsync();
        var detail = (await admin.GetAsync($"/api/admin/organizations/{shop.OrganizationId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Verified", detail["organization"]!["status"]!.GetValue<string>()); // no re-verification
        Assert.Equal(newLegalName, detail["organization"]!["legalName"]!.GetValue<string>());
        var entry = detail["statusHistory"]!.AsArray().Last()!;
        Assert.Equal("LegalDetailsChanged", entry["kind"]!.GetValue<string>());
        Assert.Equal("Verified", entry["from"]!.GetValue<string>());
        Assert.Equal("Verified", entry["to"]!.GetValue<string>());
        Assert.Contains(newLegalName, entry["reason"]!.GetValue<string>());
        Assert.NotNull(entry["by"]);
        Assert.Equal("StatusChange", detail["statusHistory"]!.AsArray().First()!["kind"]!.GetValue<string>());

        // A non-legal change adds no entry.
        var count = detail["statusHistory"]!.AsArray().Count;
        var reloaded = (await shop.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        (await shop.PutAsync("/api/organization/profile", Edit(reloaded, r => r["receiptFooter"] = "Danke!"))).Expect(HttpStatusCode.OK);
        detail = (await admin.GetAsync($"/api/admin/organizations/{shop.OrganizationId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(count, detail["statusHistory"]!.AsArray().Count);
    }

    [Fact]
    public async Task Logo_is_validated_re_encoded_served_publicly_with_cache_headers_replaced_and_removed()
    {
        var shop = await _scenario.RegisterAsync("firmenlogo", wholesaler: false);

        Assert.Equal("image.unsupported_type", (await UploadLogoAsync(shop, "GIF89a.........."u8.ToArray(), "image/png")).Code);
        Assert.Equal("image.unsupported_type",
            (await UploadLogoAsync(shop, "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml", "x.svg")).Code);
        Assert.Equal("image.too_large", (await UploadLogoAsync(shop, new byte[2 * 1024 * 1024 + 1])).Code);
        Assert.Equal("image.too_small", (await UploadLogoAsync(shop, TestImages.Png(8, 8))).Code);

        // A JPEG with EXIF (GPS marker) and a misleading name/content type.
        var upload = TestImages.Jpeg(1600, 400, exifOrientation: 1);
        var logo = (await UploadLogoAsync(shop, upload, "image/png", "logo.png")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(480, logo["width"]!.GetValue<int>());
        Assert.Equal(120, logo["height"]!.GetValue<int>());
        var screenUrl = logo["screenUrl"]!.GetValue<string>();
        var printUrl = logo["printUrl"]!.GetValue<string>();
        Assert.Equal(screenUrl, (await shop.GetAsync("/api/organization/profile")).Json["logo"]!["screenUrl"]!.GetValue<string>());

        var http = fixture.Factory.CreateClient(); // anonymous
        var (status, type, png, response) = await RawGetAsync(http, screenUrl);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("image/png", type);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(TestImages.Contains(png, TestImages.ExifSecret));
        using (var decoded = SKBitmap.Decode(png))
            Assert.Equal((480, 120), (decoded.Width, decoded.Height));
        var etag = response.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NotModified, (await RawGetAsync(http, screenUrl, etag)).Status);

        var print = await RawGetAsync(http, printUrl);
        Assert.Equal("image/jpeg", print.ContentType);
        Assert.False(TestImages.Contains(print.Body, TestImages.ExifSecret));
        Assert.False(TestImages.Contains(print.Body, "Exif"));
        Assert.Equal(HttpStatusCode.NotFound, (await RawGetAsync(http, screenUrl.Replace("/screen", "/original"))).Status);

        // Replacing keeps one active logo; the old one is gone (404), its row stays as history.
        var second = (await UploadLogoAsync(shop, TestImages.Webp(300, 300), "image/webp", "logo.webp")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(HttpStatusCode.NotFound, (await RawGetAsync(http, screenUrl)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RawGetAsync(http, second["screenUrl"]!.GetValue<string>())).Status);
        Assert.Equal(2L, await AsOwnerAsync<long>($"SELECT count(*) FROM tenancy.organization_logos WHERE organization_id = '{shop.OrganizationId}'"));
        Assert.Equal(1L, await AsOwnerAsync<long>($"SELECT count(*) FROM tenancy.organization_logos WHERE organization_id = '{shop.OrganizationId}' AND removed_at IS NULL"));

        // Removal.
        Assert.Equal(HttpStatusCode.NoContent, (await shop.SendAsync(HttpMethod.Delete, "/api/organization/logo")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await RawGetAsync(http, second["screenUrl"]!.GetValue<string>())).Status);
        Assert.Null((await shop.GetAsync("/api/organization/profile")).Json["logo"]);
        Assert.Equal("company.logo_not_found", (await shop.SendAsync(HttpMethod.Delete, "/api/organization/logo")).Code);
    }

    [Fact]
    public async Task Pos_invoice_pdf_and_public_e_receipt_carry_the_company_logo_and_texts()
    {
        var shop = await _scenario.RegisterAsync("firmenbeleg", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Pos", "Inventory", "ProductImport");
        var profile = (await shop.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        (await shop.PutAsync("/api/organization/profile", Edit(profile, r =>
        {
            r["vatId"] = $"DE{Random.Shared.Next(100_000_000, 999_999_999)}";
            r["iban"] = ValidIban;
            r["bankName"] = "Commerzbank";
            r["managingDirector"] = "Erika Muster";
            r["receiptHeader"] = "Willkommen bei uns";
            r["receiptFooter"] = "Umtausch innerhalb von 14 Tagen";
        }))).Expect(HttpStatusCode.OK);
        var logo = (await UploadLogoAsync(shop, TestImages.Png(800, 200))).Expect(HttpStatusCode.Created).Json;

        // A sale with a customer (address for the invoice).
        var warehouses = (await shop.GetAsync("/api/inventory/warehouses")).Expect(HttpStatusCode.OK).Json.AsArray();
        var warehouseId = warehouses.Count > 0
            ? Id(warehouses.First(w => w!["isDefault"]!.GetValue<bool>())!)
            : Id((await shop.PostAsync("/api/inventory/warehouses", new { code = "MAIN", name = "Hauptlager" })).Expect(HttpStatusCode.Created).Json);
        var productId = Id((await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = $"Hülle {Scenario.UniqueSuffix()}", category = "Accessory", quality = "Aftermarket", ean = Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId, delta = 3, reason = "Wareneingang", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        var register = Id((await shop.PostAsync("/api/pos/registers", new { name = "Kasse 1" })).Expect(HttpStatusCode.Created).Json);
        (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 100_00 })).Expect(HttpStatusCode.Created);
        var customer = Id((await shop.PostAsync("/api/pos/customers", new
        {
            name = "Erika Mustermann", street = "Lindenweg 3", postalCode = "10405", city = "Berlin", countryCode = "DE",
        })).Expect(HttpStatusCode.Created).Json);
        var receipt = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register,
            customerId = customer,
            lines = new[] { new { productId, quantity = 1, unitPriceMinor = 19_99, discountMinor = 0 } },
            payments = new[] { new { method = "Cash", amountMinor = 20_00 } },
        }, new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") })).Expect(HttpStatusCode.Created).Json;

        var invoice = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Expect(HttpStatusCode.Created).Json;
        var (status, mediaType, _, pdf) = await shop.GetFileAsync(invoice["pdfUrl"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/pdf", mediaType);
        Assert.Equal(1, PdfText.ImageCount(pdf));
        var lines = PdfText.Lines(pdf);
        Assert.Contains(lines, l => l.Contains("IBAN DE89 3704 0044 0532 0130 00", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Geschäftsführung: Erika Muster", StringComparison.Ordinal));

        // Public e-receipt (anonymous): logo URL and receipt texts of the issuing company.
        var token = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/public-link")).Expect(HttpStatusCode.Created).Json["token"]!.GetValue<string>();
        var view = (await fixture.CreateClient().GetAsync($"/api/public/receipts/{token}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(logo["screenUrl"]!.GetValue<string>(), view["branding"]!["logoUrl"]!.GetValue<string>());
        Assert.Equal("Willkommen bei uns", view["branding"]!["receiptHeader"]!.GetValue<string>());
        Assert.Equal("Umtausch innerhalb von 14 Tagen", view["branding"]!["receiptFooter"]!.GetValue<string>());
        Assert.DoesNotContain("DE89", view.ToJsonString()); // bank data is not part of the public receipt
    }

    [Fact]
    public async Task Database_lets_a_tenant_session_change_only_its_own_organization_and_logo()
    {
        var a = await _scenario.RegisterAsync("rlsfirma", wholesaler: false);
        var b = await _scenario.RegisterAsync("rlsandere", wholesaler: false);
        var logo = Id((await UploadLogoAsync(a, TestImages.Png(200, 100))).Expect(HttpStatusCode.Created).Json);
        var userA = await AsOwnerAsync<Guid>($"SELECT user_id FROM tenancy.memberships WHERE organization_id = '{a.OrganizationId}' LIMIT 1");

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        async Task<int> Exec(string sql)
        {
            await using var command = new NpgsqlCommand(sql, app);
            return await command.ExecuteNonQueryAsync();
        }

        await Exec($"SELECT set_config('app.current_org', '{b.OrganizationId}', false)");
        // Organizations stay readable, but B's session updates no row of A.
        await using (var read = new NpgsqlCommand($"SELECT count(*) FROM tenancy.organizations WHERE id = '{a.OrganizationId}'", app))
            Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
        Assert.Equal(0, await Exec($"UPDATE tenancy.organizations SET receipt_footer = 'gekapert' WHERE id = '{a.OrganizationId}'"));
        Assert.Equal(1, await Exec($"UPDATE tenancy.organizations SET receipt_footer = 'eigene' WHERE id = '{b.OrganizationId}'"));
        // No organization can be deleted by the app role (no DELETE policy).
        try
        {
            Assert.Equal(0, await Exec($"DELETE FROM tenancy.organizations WHERE id = '{b.OrganizationId}'"));
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            // no DELETE grant at all — also fine
        }
        Assert.Equal(1L, await AsOwnerAsync<long>($"SELECT count(*) FROM tenancy.organizations WHERE id = '{b.OrganizationId}'"));

        // Logos: everyone reads, B can neither remove A's logo nor insert one for A; nobody deletes.
        Assert.Equal(0, await Exec($"UPDATE tenancy.organization_logos SET removed_at = now(), removed_by = '{userA}' WHERE id = '{logo}'"));
        var insert = new NpgsqlCommand($"""
            INSERT INTO tenancy.organization_logos (id, organization_id, screen_storage_key, screen_width, screen_height, screen_size_bytes,
                print_storage_key, print_width, print_height, print_size_bytes, sha256, original_format, original_size_bytes, uploaded_by, uploaded_at)
            VALUES (gen_random_uuid(), '{a.OrganizationId}', 'x', 1, 1, 1, 'y', 1, 1, 1, '{new string('a', 64)}', 'Png', 1, '{userA}', now())
            """, app);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        await using (var delete = new NpgsqlCommand("DELETE FROM tenancy.organization_logos", app))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);

        // Even A's own session cannot rewrite the files behind a logo id (only removal, once).
        await Exec($"SELECT set_config('app.current_org', '{a.OrganizationId}', false)");
        await using (var rewrite = new NpgsqlCommand($"UPDATE tenancy.organization_logos SET print_storage_key = 'anders' WHERE id = '{logo}'", app))
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => rewrite.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(1, await Exec($"UPDATE tenancy.organization_logos SET removed_at = now(), removed_by = '{userA}' WHERE id = '{logo}'"));
        await using (var again = new NpgsqlCommand($"UPDATE tenancy.organization_logos SET removed_at = now() WHERE id = '{logo}'", app))
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync())).SqlState);
    }
}
