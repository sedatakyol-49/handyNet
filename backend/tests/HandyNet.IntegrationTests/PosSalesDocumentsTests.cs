using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Migrations;
using HandyNet.Modules.Notifications.Application;
using HandyNet.Persistence;
using HandyNet.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Quotations, sales invoices outside the till (shared gapless invoice series, stock, IMEI, credit notes, payments, cash at
/// the till) and gapless Z numbers — real PostgreSQL, RLS on (ADR-0015).
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosSalesDocumentsTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);
    private const string StaffPassword = "Kassierer-Passwort-1";
    private static readonly int Year = BerlinYear();

    private static int BerlinYear() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).Year;

    private static Dictionary<string, string> Key() => new() { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") };

    private static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    private static string Str(JsonNode node, string property) => node[property]!.GetValue<string>();

    private static long Long(JsonNode node, string property) => node[property]!.GetValue<long>();

    private async Task<ApiClient> ShopAsync(bool vatId = true)
    {
        var shop = await _scenario.RegisterAsync("rechnungsshop", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Pos", "Inventory", "ProductImport");
        if (vatId)
            await AsOwnerAsync<int>($"UPDATE tenancy.organizations SET vat_id = 'DE{Random.Shared.Next(100_000_000, 999_999_999)}' WHERE id = '{shop.OrganizationId}' RETURNING 1");
        return shop;
    }

    private async Task<T> AsOwnerAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> WarehouseAsync(ApiClient shop)
    {
        var warehouses = (await shop.GetAsync("/api/inventory/warehouses")).Expect(HttpStatusCode.OK).Json.AsArray();
        if (warehouses.Count > 0)
            return Id(warehouses.First(w => w!["isDefault"]!.GetValue<bool>())!);
        return Id((await shop.PostAsync("/api/inventory/warehouses", new { code = "MAIN", name = "Hauptlager" })).Expect(HttpStatusCode.Created).Json);
    }

    private static async Task<(Guid ProductId, string Ean)> StockProductAsync(ApiClient shop, int quantity, string name)
    {
        var ean = Scenario.NewEan();
        var product = (await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = $"{name} {Scenario.UniqueSuffix()}", category = "Accessory", quality = "Aftermarket", ean,
        })).Expect(HttpStatusCode.Created).Json;
        var warehouseId = await WarehouseAsync(shop);
        (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId = Id(product), delta = quantity, reason = "Wareneingang", type = "Receipt" }))
            .Expect(HttpStatusCode.OK);
        return (Id(product), ean);
    }

    private async Task<string> StockDeviceAsync(ApiClient shop, long purchasePriceMinor = 100_00)
    {
        var imei = NewImei();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var organizationId = shop.OrganizationId!.Value;
        scope.ServiceProvider.GetRequiredService<HandyNet.SharedKernel.Tenancy.TenantContext>().SetSystemActor(organizationId);
        var catalog = scope.ServiceProvider.GetRequiredService<HandyNet.Modules.Catalog.Contracts.IProductCatalog>();
        var inventory = scope.ServiceProvider.GetRequiredService<HandyNet.Modules.Inventory.Contracts.IInventory>();
        var product = await catalog.FindOrStageDeviceAsync("TestPhone", "Modell " + Scenario.UniqueSuffix(), "128 GB", organizationId, CancellationToken.None);
        var warehouseId = await inventory.GetOrStageDefaultWarehouseAsync(CancellationToken.None);
        var unit = await inventory.StageReceiveUnitAsync(warehouseId, product.Value.Id, imei, purchasePriceMinor,
            HandyNet.Modules.Inventory.Domain.TaxScheme.Margin25a, "test:direct", CancellationToken.None);
        Assert.True(unit.IsSuccess, unit.Error?.Message);
        await scope.ServiceProvider.GetRequiredService<HandyNetDbContext>().SaveChangesAsync();
        return imei;
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

    private static async Task<int> AvailableAsync(ApiClient shop, string ean, Guid register) =>
        (await shop.GetAsync($"/api/pos/lookup?code={ean}&registerId={register}")).Expect(HttpStatusCode.OK).Json["available"]!.GetValue<int>();

    private static async Task<Guid> RegisterAsync(ApiClient shop, string name = "Kasse 1") =>
        Id((await shop.PostAsync("/api/pos/registers", new { name })).Expect(HttpStatusCode.Created).Json);

    private static async Task<Guid> OpenSessionAsync(ApiClient client, Guid register, long openingCashMinor = 100_00) =>
        Id((await client.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor })).Expect(HttpStatusCode.Created).Json);

    private static async Task<Guid> CustomerAsync(ApiClient shop, string? email = null, string? company = "Muster Handel GmbH", string? vatId = "DE123456789") =>
        Id((await shop.PostAsync("/api/pos/customers", new
        {
            name = "Max Muster", company, email, street = "Gewerbering 9", postalCode = "20095", city = "Hamburg", countryCode = "DE", vatId,
        })).Expect(HttpStatusCode.Created).Json);

    private static object Line(Guid? productId = null, string? imei = null, string? description = null, int quantity = 1, long price = 10_00,
        long discount = 0, int vat = 1900) => new
    {
        productId, imei, description, note = (string?)null, quantity, unitPriceMinor = price, discountMinor = discount, vatRateBasisPoints = vat,
    };

    private static async Task<JsonNode> DraftAsync(ApiClient shop, Guid? customerId, object[] lines, string priceMode = "Gross", int paymentTermDays = 14,
        bool reverseCharge = false) =>
        (await shop.PostAsync("/api/pos/sales-invoices", new { customerId, lines, priceMode, paymentTermDays, reverseCharge })).Expect(HttpStatusCode.Created).Json;

    private static async Task<JsonNode> IssueAsync(ApiClient shop, Guid invoiceId) =>
        (await shop.PostAsync($"/api/pos/sales-invoices/{invoiceId}/issue")).Expect(HttpStatusCode.Created).Json;

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

    // ---- quotations -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Quotation_is_numbered_computed_in_net_mode_emailed_with_pdf_duplicated_and_converted_once()
    {
        var shop = await ShopAsync();
        var (product, _) = await StockProductAsync(shop, 10, "Hülle");
        var email = $"einkauf-{Scenario.UniqueSuffix()}@example.de";
        var customer = await CustomerAsync(shop, email);

        // Net prices: 2 × 10.00 € at 19 % and a free text line 50.00 € at 7 %, 5.00 € discount on the first line.
        var created = (await shop.PostAsync("/api/pos/quotations", new
        {
            customerId = customer,
            priceMode = "Net",
            lines = new[] { Line(product, quantity: 2, price: 10_00, discount: 5_00), Line(description: "Einrichtung vor Ort", price: 50_00, vat: 700) },
            notes = "Lieferung innerhalb einer Woche.",
        })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal($"AN-{Year}-0001", Str(created, "number"));
        Assert.Equal("Draft", Str(created, "status"));
        Assert.Equal("Muster Handel GmbH", created["customer"]!["company"]!.GetValue<string>());
        // 15.00 net + 2.85 VAT; 50.00 net + 3.50 VAT → 71.35 gross.
        Assert.Equal((65_00L, 6_35L, 71_35L), (Long(created, "totalNetMinor"), Long(created, "totalVatMinor"), Long(created, "totalGrossMinor")));
        Assert.Equal(2, created["vat"]!.AsArray().Count);

        // Validation: unknown VAT rate, sub-cent amounts cannot be expressed (integers), no customer.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync("/api/pos/quotations", new
        {
            customerId = customer, lines = new[] { Line(description: "X", vat: 1600) },
        })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync("/api/pos/quotations", new { lines = new[] { Line(description: "X") } })).Status);

        var pdf = await shop.GetFileAsync(Str(created, "pdfUrl"));
        Assert.Equal("application/pdf", pdf.MediaType);
        var text = PdfText.All(pdf.Body);
        foreach (var expected in new[] { "Angebot", $"AN-{Year}-0001", "Gültig bis", "Muster Handel GmbH", "Netto (19 %)", "Umsatzsteuer 7 %", "71,35 €" })
            Assert.Contains(expected, text);

        // Email to the customer's address: PDF attached via the notifications outbox, status becomes Sent.
        (await shop.PostAsync($"/api/pos/quotations/{Id(created)}/email", new { email = (string?)null })).Expect(HttpStatusCode.Accepted);
        await ProcessEmailsUntilAsync(() => fixture.Emails.SentAttachments.Any(a => a.To == email));
        var mail = fixture.Emails.SentAttachments.Single(a => a.To == email);
        Assert.Equal(($"AN-{Year}-0001.pdf", "application/pdf"), (mail.FileName, mail.ContentType));
        Assert.Contains("Angebot", PdfText.All(mail.Content));
        Assert.Equal("Sent", Str((await shop.GetAsync($"/api/pos/quotations/{Id(created)}")).Expect(HttpStatusCode.OK).Json, "status"));

        var copy = (await shop.PostAsync($"/api/pos/quotations/{Id(created)}/duplicate")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal($"AN-{Year}-0002", Str(copy, "number"));
        Assert.Equal("Draft", Str(copy, "status"));
        Assert.Equal(Long(created, "totalGrossMinor"), Long(copy, "totalGrossMinor"));

        // Convert: an invoice draft with the same lines; the quotation is accepted and final; converting again returns the draft.
        var draft = (await shop.PostAsync($"/api/pos/quotations/{Id(created)}/convert")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("Draft", "Net", 71_35L), (Str(draft, "status"), Str(draft, "priceMode"), Long(draft, "totalGrossMinor")));
        Assert.Equal($"AN-{Year}-0001", Str(draft, "quotationNumber"));
        var again = (await shop.PostAsync($"/api/pos/quotations/{Id(created)}/convert")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(draft), Id(again));
        var accepted = (await shop.GetAsync($"/api/pos/quotations/{Id(created)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("Accepted", false), (Str(accepted, "status"), accepted["editable"]!.GetValue<bool>()));
        Assert.Equal("pos.quotation_not_editable", (await shop.PutAsync($"/api/pos/quotations/{Id(created)}", new
        {
            customerId = customer, lines = new[] { Line(description: "X") },
        })).Code);

        var list = (await shop.GetAsync("/api/pos/quotations?status=Accepted")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Id(created), Id(list["items"]!.AsArray().Single()!));
        Assert.Single((await shop.GetAsync("/api/pos/quotations?search=0002")).Json["items"]!.AsArray());
    }

    [Fact]
    public async Task Quotation_email_follows_the_company_email_language_and_the_language_of_the_request()
    {
        var shop = await ShopAsync();
        var customerEmail = $"sprache-{Scenario.UniqueSuffix()}@example.de";
        var customer = await CustomerAsync(shop, customerEmail);
        var quotation = (await shop.PostAsync("/api/pos/quotations", new { customerId = customer, lines = new[] { Line(description: "Service") } }))
            .Expect(HttpStatusCode.Created).Json;
        var path = $"/api/pos/quotations/{Id(quotation)}/email";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.PostAsync(path, new { email = customerEmail, language = "xx" })).Status);

        // Default: German. Company language English. Explicit Turkish wins over the company setting.
        (await shop.PostAsync(path, new { email = customerEmail })).Expect(HttpStatusCode.Accepted);
        await ProcessEmailsUntilAsync(() => fixture.Emails.Sent.Count(e => e.To == customerEmail) == 1);
        await shop.SetEmailLanguageAsync("en");
        (await shop.PostAsync(path, new { email = customerEmail })).Expect(HttpStatusCode.Accepted);
        (await shop.PostAsync(path, new { email = customerEmail, language = "tr" })).Expect(HttpStatusCode.Accepted);
        await ProcessEmailsUntilAsync(() => fixture.Emails.Sent.Count(e => e.To == customerEmail) == 3);

        var bodies = fixture.Emails.Sent.Where(e => e.To == customerEmail).Select(e => e.Body).ToList();
        Assert.Contains("anbei erhalten Sie unser Angebot", bodies[0]);
        Assert.Contains(bodies, b => b.Contains("Please find attached our quotation"));
        Assert.Contains(bodies, b => !b.Contains("anbei erhalten") && !b.Contains("Please find attached"));
    }

    // ---- sales invoices: shared series, stock, IMEI, idempotency, immutability -------------------------------------------------

    [Fact]
    public async Task Invoices_share_one_gapless_series_with_POS_invoices_and_issuing_moves_stock_once()
    {
        var shop = await ShopAsync();
        var register = await RegisterAsync(shop);
        await OpenSessionAsync(shop, register);
        var (product, ean) = await StockProductAsync(shop, 10, "Ladekabel");
        var imei = await StockDeviceAsync(shop, purchasePriceMinor: 200_00);
        var customer = await CustomerAsync(shop);

        // 1. An invoice for a POS sale takes RE-…-000001.
        var receipt = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, customerId = customer,
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 9_99, discountMinor = 0 } },
            payments = new[] { new { method = "Card", amountMinor = 9_99 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        var posInvoice = (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal($"RE-{Year}-000001", Str(posInvoice, "number"));
        Assert.Equal(9, await AvailableAsync(shop, ean, register));

        // 2. A sales invoice: 3 cables + a §25a device + a free text line.
        var draft = await DraftAsync(shop, customer,
            [Line(product, quantity: 3, price: 11_90), Line(imei: imei, price: 350_00), Line(description: "Versand", price: 5_95)]);
        Assert.Equal("Draft", Str(draft, "status"));
        Assert.Null(draft["number"]);
        Assert.True(draft["lines"]!.AsArray()[1]!["margin25a"]!.GetValue<bool>());
        Assert.Equal(9, await AvailableAsync(shop, ean, register)); // a draft moves no stock

        var issued = await IssueAsync(shop, Id(draft));
        Assert.Equal($"RE-{Year}-000002", Str(issued, "number"));
        Assert.Equal(("Issued", "Open"), (Str(issued, "status"), Str(issued, "paymentState")));
        Assert.Equal(6, await AvailableAsync(shop, ean, register));
        Assert.Equal(HttpStatusCode.NotFound, (await shop.GetAsync($"/api/pos/lookup?code={imei}&registerId={register}")).Status); // sold
        // §25a: gross only, its VAT is never shown; shown VAT = 19 % of cables + shipping only.
        Assert.Equal(350_00, Long(issued, "marginGrossMinor"));
        Assert.Equal(41_65 + 350_00, Long(issued, "totalGrossMinor"));
        Assert.Equal(6_65, Long(issued, "totalVatMinor")); // 41.65 − round(41.65 / 1.19) = 6.65
        Assert.DoesNotContain("purchasePrice", issued.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("marginDetails", issued.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var dueDate = DateOnly.Parse(Str(issued, "dueDate"));
        Assert.Equal(DateOnly.Parse(Str(issued, "invoiceDate")).AddDays(14), dueDate);

        // Idempotent: issuing again returns the same invoice and moves nothing.
        var again = (await shop.PostAsync($"/api/pos/sales-invoices/{Id(draft)}/issue")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Str(issued, "number"), Str(again, "number"));
        Assert.Equal(6, await AvailableAsync(shop, ean, register));

        var text = PdfText.All((await shop.GetFileAsync(Str(issued, "pdfUrl"))).Body);
        foreach (var expected in new[] { "Rechnung", $"RE-{Year}-000002", "Rechnungsdatum", "Leistungsdatum", "Fällig am", "Max Muster", "Gewerbering 9",
                     "20095 Hamburg", "USt-IdNr. Empfänger", "DE123456789", "USt-IdNr.:", "Differenzbesteuert (§25a)", "Gebrauchtgegenstände/Sonderregelung",
                     "Umsatzsteuer 19 %", $"IMEI {imei}" })
            Assert.Contains(expected, text);

        // 3. The next POS invoice continues the same series.
        var receipt2 = (await shop.PostAsync("/api/pos/sales", new
        {
            registerId = register, customerId = customer,
            lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 9_99, discountMinor = 0 } },
            payments = new[] { new { method = "Card", amountMinor = 9_99 } },
        }, Key())).Expect(HttpStatusCode.Created).Json;
        Assert.Equal($"RE-{Year}-000003", Str((await shop.PostAsync($"/api/pos/sales/{Id(receipt2)}/invoice", new { })).Expect(HttpStatusCode.Created).Json, "number"));

        // The list shows both origins, newest first.
        var list = (await shop.GetAsync("/api/pos/sales-invoices")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal([$"RE-{Year}-000003", $"RE-{Year}-000002", $"RE-{Year}-000001"], list.Select(i => Str(i!, "number")).ToArray());
        Assert.Equal(["Receipt", "Sale", "Receipt"], list.Select(i => Str(i!, "origin")).ToArray());
        Assert.Single((await shop.GetAsync($"/api/pos/sales-invoices?search=000002")).Json["items"]!.AsArray());
    }

    [Fact]
    public async Task Refused_issues_take_no_number_and_issued_invoices_are_immutable()
    {
        var shop = await ShopAsync();
        var register = await RegisterAsync(shop);
        var (product, ean) = await StockProductAsync(shop, 2, "Netzteil");
        var customer = await CustomerAsync(shop);

        // Insufficient stock: refused, nothing moves, no number used.
        var tooMany = await DraftAsync(shop, customer, [Line(product, quantity: 3)]);
        Assert.Equal("stock.insufficient", (await shop.PostAsync($"/api/pos/sales-invoices/{Id(tooMany)}/issue")).Code);
        Assert.Equal(2, await AvailableAsync(shop, ean, register));
        // Incomplete customer address, no customer at all.
        var noAddress = (await shop.PostAsync("/api/pos/sales-invoices", new
        {
            customer = new { name = "Laufkunde" }, lines = new[] { Line(description: "Reparatur", price: 49_00) },
        })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("pos.invoice_customer_address", (await shop.PostAsync($"/api/pos/sales-invoices/{Id(noAddress)}/issue")).Code);
        var noCustomer = await DraftAsync(shop, null, [Line(description: "Reparatur", price: 49_00)]);
        Assert.Equal("pos.invoice_customer_required", (await shop.PostAsync($"/api/pos/sales-invoices/{Id(noCustomer)}/issue")).Code);
        // Reverse charge needs 0 % lines (and the customer's VAT id).
        Assert.Equal("pos.reverse_charge_lines", (await shop.PostAsync("/api/pos/sales-invoices", new
        {
            customerId = customer, reverseCharge = true, lines = new[] { Line(description: "Handys", price: 5000_00) },
        })).Code);

        var ok = await DraftAsync(shop, customer, [Line(product, quantity: 2, price: 20_00)]);
        var issued = await IssueAsync(shop, Id(ok));
        Assert.Equal($"RE-{Year}-000001", Str(issued, "number")); // the refusals above did not use a number
        Assert.Equal(0, await AvailableAsync(shop, ean, register));

        // Final: no edit, no discard via the API; no UPDATE/DELETE even for the owner role.
        Assert.Equal("pos.sales_invoice_issued", (await shop.PutAsync($"/api/pos/sales-invoices/{Id(ok)}", new
        {
            customerId = customer, lines = new[] { Line(description: "X") },
        })).Code);
        Assert.Equal("pos.sales_invoice_issued", (await shop.PostAsync($"/api/pos/sales-invoices/{Id(ok)}/discard")).Code);
        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<int>($"UPDATE pos.sales_invoices SET notes = 'geändert' WHERE id = '{Id(ok)}' RETURNING 1"));
        Assert.Equal("42501", update.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"DELETE FROM pos.sales_invoices WHERE id = '{Id(ok)}' RETURNING 1"));
        Assert.Equal("42501", delete.SqlState);

        // Gapless: a number taken without its invoice fails at commit; an issued row must carry the number just taken.
        var gap = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            UPDATE pos.invoice_sequences SET next_number = next_number + 1 WHERE organization_id = '{shop.OrganizationId}' AND year = {Year} RETURNING 1
            """));
        Assert.Equal("23514", gap.SqlState);
        var draft = await DraftAsync(shop, customer, [Line(description: "Service", price: 10_00)]);
        var wrongNumber = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            UPDATE pos.sales_invoices SET status = 'Issued', year = {Year}, sequence_number = 7, number = 'RE-{Year}-000007', invoice_date = current_date,
                   due_date = current_date, service_date = current_date, document = '{"{}"}', issued_at = now(), issued_by = created_by
            WHERE id = '{Id(draft)}' RETURNING 1
            """));
        Assert.Equal("23514", wrongNumber.SqlState);

        // Discarding a draft keeps the row (never deleted) and hides it from the default list.
        (await shop.PostAsync($"/api/pos/sales-invoices/{Id(draft)}/discard")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain((await shop.GetAsync("/api/pos/sales-invoices")).Json["items"]!.AsArray(), i => Id(i!) == Id(draft));
    }

    [Fact]
    public async Task Concurrent_issues_and_POS_invoices_get_unique_gapless_numbers()
    {
        var shop = await ShopAsync();
        var register = await RegisterAsync(shop);
        await OpenSessionAsync(shop, register);
        var (product, _) = await StockProductAsync(shop, 50, "Displayfolie");
        var customer = await CustomerAsync(shop);
        var drafts = new List<Guid>();
        for (var i = 0; i < 8; i++)
            drafts.Add(Id(await DraftAsync(shop, customer, [Line(product, price: 5_00)])));
        var receipts = new List<Guid>();
        for (var i = 0; i < 4; i++)
            receipts.Add(Id((await shop.PostAsync("/api/pos/sales", new
            {
                registerId = register, customerId = customer,
                lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 5_00, discountMinor = 0 } },
                payments = new[] { new { method = "Card", amountMinor = 5_00 } },
            }, Key())).Expect(HttpStatusCode.Created).Json));

        // Every draft issued twice at once (idempotency under concurrency) plus the POS invoices.
        var tasks = drafts.SelectMany(d => new[] { shop.PostAsync($"/api/pos/sales-invoices/{d}/issue"), shop.PostAsync($"/api/pos/sales-invoices/{d}/issue") })
            .Concat(receipts.Select(r => shop.PostAsync($"/api/pos/sales/{r}/invoice", new { })))
            .ToList();
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"{r.Status}: {r.Code}"));
        var numbers = responses.Select(r => Str(r.Json, "number")).Distinct().Order().ToList();
        Assert.Equal(Enumerable.Range(1, 12).Select(n => $"RE-{Year}-{n:D6}").ToList(), numbers);
        // Each draft got exactly one number.
        foreach (var d in drafts)
            Assert.Single(responses.Where(r => r.Json["id"]?.GetValue<string>() == d.ToString()).Select(r => Str(r.Json, "number")).Distinct());
    }

    // ---- credit notes, payments, cash at the till ------------------------------------------------------------------------------

    [Fact]
    public async Task Credit_note_mirrors_the_invoice_returns_stock_and_needs_the_refund_right_or_an_approval()
    {
        var shop = await ShopAsync();
        var register = await RegisterAsync(shop);
        var (product, ean) = await StockProductAsync(shop, 5, "Akku");
        var imei = await StockDeviceAsync(shop);
        var customer = await CustomerAsync(shop);
        var invoice = await IssueAsync(shop, Id(await DraftAsync(shop, customer, [Line(product, quantity: 2, price: 30_00), Line(imei: imei, price: 199_00)])));
        Assert.Equal(3, await AvailableAsync(shop, ean, register));

        // A cashier without pos.refund is refused; with a manager approval (PIN) it works and is recorded.
        var (staff, staffId) = await StaffAsync(shop, "Staff", "Kassierer Tom");
        var (_, managerId) = await StaffAsync(shop, "Manager", "Managerin Ida");
        (await shop.PutAsync($"/api/organization/staff/{staffId}/pos-rights", new { permissions = Array.Empty<string>(), maxDiscountPercent = (int?)null }))
            .Expect(HttpStatusCode.OK);
        Assert.Equal("permission.denied", (await staff.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/credit-note", new { reason = "Reklamation" })).Code);
        // Everyone with pos.sell creates and issues invoices.
        var staffDraft = await DraftAsync(staff, customer, [Line(description: "Beratung", price: 10_00)]);
        Assert.Equal("Issued", Str(await IssueAsync(staff, Id(staffDraft)), "status"));

        (await shop.PutAsync($"/api/pos/cashiers/{managerId}/pin", new { pin = "2580" })).Expect(HttpStatusCode.OK);
        var approval = (await staff.PostAsync($"/api/pos/registers/{register}/approvals", new { approverUserId = managerId, pin = "2580", permissions = new[] { "pos.refund" } }))
            .Expect(HttpStatusCode.Created).Json;
        var approved = new Dictionary<string, string> { ["X-Pos-Approval"] = Str(approval, "token") };
        Assert.Equal("pos.approval_invalid",
            (await staff.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/credit-note", new { reason = "Reklamation" }, approved)).Code); // no register
        var note = (await staff.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/credit-note", new { reason = "Reklamation", registerId = register }, approved))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("CreditNote", $"RE-{Year}-000003"), (Str(note, "kind"), Str(note, "number")));
        Assert.Equal(-Long(invoice, "totalGrossMinor"), Long(note, "totalGrossMinor"));
        Assert.Equal(-Long(invoice, "totalVatMinor"), Long(note, "totalVatMinor"));
        Assert.Equal(Str(invoice, "number"), Str(note, "originalNumber"));
        Assert.Equal("CreditNote", await AsOwnerAsync<string>($"SELECT used_for FROM pos.approvals WHERE id = '{Id(approval, "approvalId")}'"));

        // Stock back, the device is in stock again; the invoice reads as cancelled; repeat returns the same note.
        Assert.Equal(5, await AvailableAsync(shop, ean, register));
        Assert.Equal(HttpStatusCode.OK, (await shop.GetAsync($"/api/pos/lookup?code={imei}&registerId={register}")).Status);
        var cancelled = (await shop.GetAsync($"/api/pos/sales-invoices/{Id(invoice)}")).Expect(HttpStatusCode.OK).Json;
        Assert.True(cancelled["cancelled"]!.GetValue<bool>());
        Assert.Equal(Id(note), Id(cancelled, "creditNoteId"));
        Assert.Equal(Id(note), Id((await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/credit-note", new { reason = "nochmal" })).Expect(HttpStatusCode.OK).Json));
        Assert.Equal("pos.sales_invoice_cancelled", (await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/payments",
            new { method = "BankTransfer", amountMinor = 1_00 })).Code);
        var text = PdfText.All((await shop.GetFileAsync(Str(note, "pdfUrl"))).Body);
        Assert.Contains("Stornorechnung", text);
        Assert.Contains($"Stornorechnung zur Rechnung {Str(invoice, "number")}", text);
        Assert.Contains("Grund: Reklamation", text);
    }

    private async Task<(ApiClient Client, Guid UserId)> StaffAsync(ApiClient owner, string role, string name)
    {
        var email = $"kasse-{Scenario.UniqueSuffix()}@test.de";
        var userId = Id((await owner.PostAsync("/api/organization/staff", new { email, displayName = name, password = StaffPassword, role }))
            .Expect(HttpStatusCode.OK).Json, "userId");
        var client = fixture.CreateClient();
        await client.LoginAsync(email, StaffPassword);
        client.OrganizationId = owner.OrganizationId;
        return (client, userId);
    }

    [Fact]
    public async Task Payments_are_append_only_never_exceed_the_total_and_cash_goes_through_the_till()
    {
        var shop = await ShopAsync();
        var customer = await CustomerAsync(shop);
        var invoice = await IssueAsync(shop, Id(await DraftAsync(shop, customer, [Line(description: "Großhandel Paket", quantity: 10, price: 10_00)])));
        var id = Id(invoice);

        Assert.Equal("pos.cash_payment_at_till", (await shop.PostAsync($"/api/pos/sales-invoices/{id}/payments", new { method = "Cash", amountMinor = 10_00 })).Code);
        var partial = (await shop.PostAsync($"/api/pos/sales-invoices/{id}/payments",
            new { method = "BankTransfer", amountMinor = 40_00, reference = "Überweisung 4711" })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("PartiallyPaid", 40_00L, 60_00L), (Str(partial, "paymentState"), Long(partial, "paidMinor"), Long(partial, "openMinor")));
        Assert.Equal("pos.payment_exceeds_open", (await shop.PostAsync($"/api/pos/sales-invoices/{id}/payments",
            new { method = "BankTransfer", amountMinor = 60_01 })).Code);
        // The database refuses an overpayment and a direct cash row even for the owner role, and payments never change.
        var over = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            INSERT INTO pos.sales_invoice_payments (id, organization_id, invoice_id, method, amount_minor, paid_on, created_at, created_by)
            VALUES (gen_random_uuid(), '{shop.OrganizationId}', '{id}', 'BankTransfer', 60_01, current_date, now(), gen_random_uuid()) RETURNING 1
            """.Replace("60_01", "6001")));
        Assert.Equal("23514", over.SqlState);
        var cash = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            INSERT INTO pos.sales_invoice_payments (id, organization_id, invoice_id, method, amount_minor, paid_on, created_at, created_by)
            VALUES (gen_random_uuid(), '{shop.OrganizationId}', '{id}', 'Cash', 100, current_date, now(), gen_random_uuid()) RETURNING 1
            """));
        Assert.Equal("23514", cash.SqlState);
        var change = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"UPDATE pos.sales_invoice_payments SET amount_minor = 1 WHERE invoice_id = '{id}' RETURNING 1"));
        Assert.Equal("42501", change.SqlState);

        var paid = (await shop.PostAsync($"/api/pos/sales-invoices/{id}/payments", new { method = "Card", amountMinor = 60_00 })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal(("Paid", 0L), (Str(paid, "paymentState"), Long(paid, "openMinor")));
        Assert.Equal(2, paid["payments"]!.AsArray().Count);
        Assert.Single((await shop.GetAsync("/api/pos/sales-invoices?paymentState=Paid&kind=Invoice")).Json["items"]!.AsArray(), i => Str(i!, "origin") == "Sale");
    }

    [Fact]
    public async Task Collecting_at_the_till_writes_a_signed_receipt_without_moving_stock_and_shows_on_the_z_report()
    {
        var shop = await ShopAsync();
        var admin = await _scenario.AdminAsync();
        (await admin.PutAsync($"/api/admin/pos/fiscal/{shop.OrganizationId}", new { mode = "SimulatedTse" })).Expect(HttpStatusCode.OK);
        var register = await RegisterAsync(shop);
        var session = await OpenSessionAsync(shop, register, 20_00);
        var (product, ean) = await StockProductAsync(shop, 10, "Kopfhörer");
        var customer = await CustomerAsync(shop);
        var invoice = await IssueAsync(shop, Id(await DraftAsync(shop, customer, [Line(product, quantity: 4, price: 25_00)])));
        Assert.Equal(6, await AvailableAsync(shop, ean, register));

        // Partial cash (60 € of 100 €, 70 € tendered) with an Idempotency-Key; a retry returns the same receipt.
        var key = Key();
        var request = new { registerId = register, amountMinor = 60_00, payments = new[] { new { method = "Cash", amountMinor = 70_00 } } };
        var collected = (await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/collect", request, key)).Expect(HttpStatusCode.Created).Json;
        var receipt = collected["receipt"]!;
        Assert.Equal(("InvoicePayment", 60_00L, 10_00L), (Str(receipt, "kind"), Long(receipt, "totalGrossMinor"), Long(receipt, "changeMinor")));
        Assert.Equal("Signed", receipt["fiscal"]!["status"]!.GetValue<string>());
        Assert.Contains("^0.00_0.00_0.00_0.00_60.00^", receipt["fiscal"]!["processData"]!.GetValue<string>()); // not taxable: 0 % bucket
        Assert.Equal($"Zahlung Rechnung {Str(invoice, "number")}", Str(receipt["lines"]!.AsArray().Single()!, "description"));
        Assert.Null(receipt["lines"]!.AsArray().Single()!["productId"]);
        Assert.Equal(Id(receipt), Id((await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/collect", request, key)).Expect(HttpStatusCode.OK).Json["receipt"]!));
        Assert.Equal(6, await AvailableAsync(shop, ean, register)); // no second stock deduction

        var state = (await shop.GetAsync($"/api/pos/sales-invoices/{Id(invoice)}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("PartiallyPaid", 40_00L), (Str(state, "paymentState"), Long(state, "openMinor")));
        var payment = state["payments"]!.AsArray().Single()!;
        Assert.Equal(("Cash", Id(receipt)), (Str(payment, "method"), Id(payment, "receiptId")));

        // The rest by card at the till (null amount = what is open); then nothing more can be collected.
        (await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/collect", new
        {
            registerId = register, amountMinor = (long?)null, payments = new[] { new { method = "Card", amountMinor = 40_00 } },
        }, Key())).Expect(HttpStatusCode.Created);
        Assert.Equal("pos.sales_invoice_paid", (await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/collect", new
        {
            registerId = register, payments = new[] { new { method = "Cash", amountMinor = 1_00 } },
        }, Key())).Code);
        // A payment receipt is not a sale: no storno/return, no "invoice for a POS sale".
        Assert.Equal("pos.not_a_sale", (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/storno", new { reason = "x" })).Code);
        Assert.Equal("pos.invoice_not_a_sale", (await shop.PostAsync($"/api/pos/sales/{Id(receipt)}/invoice", new { })).Code);

        // Z-report: payments on invoices counted separately; expected cash includes the 60 €; the Z number is assigned.
        var live = (await shop.GetAsync($"/api/pos/sessions/{session}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((2, 100_00L), (live["summary"]!["invoicePaymentCount"]!.GetValue<int>(), Long(live["summary"]!, "invoicePaymentsMinor")));
        Assert.Equal(80_00, Long(live, "expectedCashMinor"));
        var closed = (await shop.PostAsync($"/api/pos/sessions/{session}/close", new { countedCashMinor = 80_00 })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, Long(closed, "zNumber"));
        Assert.Contains(closed["summary"]!["byTaxTreatment"]!.AsArray(), t => Str(t!, "treatment") == "NotTaxable" && Long(t!, "grossMinor") == 100_00);

        // A receipt of this kind without its invoice payment cannot be committed (deferred check); owner role, raw SQL.
        Assert.Equal(1L, await AsOwnerAsync<long>($"SELECT count(*) FROM pos.sales_invoice_payments WHERE receipt_id = '{Id(receipt)}'"));
    }

    // ---- overdue, tenant isolation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Overdue_invoices_are_listed_and_counted_and_other_tenants_see_nothing()
    {
        var shop = await ShopAsync();
        var customer = await CustomerAsync(shop);
        var invoice = await IssueAsync(shop, Id(await DraftAsync(shop, customer, [Line(description: "Wartung", price: 120_00)], paymentTermDays: 0)));
        Assert.False(invoice["overdue"]!.GetValue<bool>()); // due today
        // Move the due date into the past (test only: triggers disabled for this owner session).
        await AsOwnerAsync<int>($"""
            SET session_replication_role = replica;
            UPDATE pos.sales_invoices SET invoice_date = invoice_date - 20, due_date = due_date - 10, service_date = service_date - 20 WHERE id = '{Id(invoice)}';
            SELECT 1;
            """);
        var overdue = (await shop.GetAsync("/api/pos/sales-invoices?overdue=true")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal(Id(invoice), Id(overdue.Single()!));
        var summary = (await shop.GetAsync("/api/pos/sales-invoices/summary")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal((1, 120_00L), (summary["overdueCount"]!.GetValue<int>(), Long(summary, "overdueMinor")));
        (await shop.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/payments", new { method = "BankTransfer", amountMinor = 120_00 })).Expect(HttpStatusCode.Created);
        Assert.Empty((await shop.GetAsync("/api/pos/sales-invoices?overdue=true")).Json["items"]!.AsArray());

        var quotation = (await shop.PostAsync("/api/pos/quotations", new { customerId = customer, lines = new[] { Line(description: "X") } }))
            .Expect(HttpStatusCode.Created).Json;
        var foreign = await ShopAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/pos/sales-invoices/{Id(invoice)}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetFileAsync($"/api/pos/sales-invoices/{Id(invoice)}/pdf")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/api/pos/sales-invoices/{Id(invoice)}/issue")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/pos/quotations/{Id(quotation)}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/api/pos/quotations/{Id(quotation)}/convert")).Status);
        Assert.Empty((await foreign.GetAsync("/api/pos/sales-invoices")).Json["items"]!.AsArray());
        // Using another tenant's customer is refused.
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync("/api/pos/quotations", new { customerId = customer, lines = new[] { Line(description: "X") } })).Status);

        // The app role without a tenant sees no rows; it cannot delete any.
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        foreach (var table in new[] { "quotations", "sales_invoices", "sales_invoice_payments", "sales_document_emails", "quotation_sequences" })
        {
            await using var read = new NpgsqlCommand($"SELECT count(*) FROM pos.{table}", app);
            Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
            await using var delete = new NpgsqlCommand($"DELETE FROM pos.{table}", app);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [Fact]
    public async Task Sales_documents_need_the_pos_module()
    {
        var shop = await _scenario.RegisterAsync("ohnekasse", wholesaler: false);
        await _scenario.ApproveAsync(shop, "Inventory");
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/pos/sales-invoices")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.PostAsync("/api/pos/quotations", new { lines = Array.Empty<object>() })).Status);
    }

    // ---- Z numbers ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Z_numbers_are_gapless_per_register_shown_on_sessions_and_exported_as_Z_NR()
    {
        var shop = await ShopAsync();
        var first = await RegisterAsync(shop, "Kasse A");
        var second = await RegisterAsync(shop, "Kasse B");
        var numbers = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            var session = await OpenSessionAsync(shop, first, 0);
            Assert.Null((await shop.GetAsync($"/api/pos/sessions/{session}")).Json["zNumber"]);
            numbers.Add(Long((await shop.PostAsync($"/api/pos/sessions/{session}/close", new { countedCashMinor = 0 })).Expect(HttpStatusCode.OK).Json, "zNumber"));
        }
        var other = await OpenSessionAsync(shop, second, 0);
        var otherZ = Long((await shop.PostAsync($"/api/pos/sessions/{other}/close", new { countedCashMinor = 0 })).Expect(HttpStatusCode.OK).Json, "zNumber");
        Assert.Equal([1L, 2L], numbers);
        Assert.Equal(1, otherZ);
        var listed = (await shop.GetAsync($"/api/pos/sessions?registerId={first}")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();
        Assert.Equal([2L, 1L], listed.Select(s => Long(s!, "zNumber")).ToArray());

        // Database: the counter moves by +1 only and every number taken must close a session (no gaps, even for the owner).
        var gap = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<int>($"UPDATE pos.registers SET next_z_number = next_z_number + 1 WHERE id = '{first}' RETURNING 1"));
        Assert.Equal("23514", gap.SqlState);
        var skip = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwnerAsync<int>($"UPDATE pos.registers SET next_z_number = next_z_number + 2 WHERE id = '{first}' RETURNING 1"));
        Assert.Equal("23514", skip.SqlState);
        var open = await OpenSessionAsync(shop, first, 0);
        var wrong = await Assert.ThrowsAsync<PostgresException>(() => AsOwnerAsync<int>($"""
            UPDATE pos.cash_sessions SET status = 'Closed', z_number = 9, closed_at = now(), closed_by = opened_by, counted_cash_minor = 0,
                   expected_cash_minor = 0, difference_minor = 0, summary = '{"{}"}' WHERE id = '{open}' RETURNING 1
            """));
        Assert.Equal("23514", wrong.SqlState);
        (await shop.PostAsync($"/api/pos/sessions/{open}/close", new { countedCashMinor = 0 })).Expect(HttpStatusCode.OK);

        // DSFinV-K: Z_NR of cashpointclosing.csv is the stored Z number.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).DateTime);
        var export = await shop.GetFileAsync($"/api/pos/exports/dsfinvk?registerId={first}&from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, export.Status);
        using var zip = new ZipArchive(new MemoryStream(export.Body));
        using var reader = new StreamReader(zip.GetEntry("cashpointclosing.csv")!.Open(), Encoding.UTF8);
        var rows = (await reader.ReadToEndAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.Equal(["1", "2", "3"], rows.Select(r => r.Split(',')[2].Trim('"')).Order().ToArray());
    }

    [Fact]
    public async Task Migration_backfills_z_numbers_of_closed_sessions_in_closing_order()
    {
        var database = $"handynet_test_znr_{Guid.NewGuid():N}"[..40];
        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }
        var connection = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database }.ConnectionString;
        try
        {
            await using (var db = DatabaseMigrator.CreateContext(connection))
                await db.GetService<IMigrator>().MigrateAsync("20261023190450_CompanyProfileAndLogo");

            var user = Guid.NewGuid();
            var org = Guid.NewGuid();
            var warehouse = Guid.NewGuid();
            var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
            // Register A: closed on day 3, day 1 and day 2 (inserted out of order) and one open session; register B: one closing.
            var (s3, s1, s2, sOpen, sb) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            string Closed(Guid id, Guid register, int day) => $"""
                ('{id}', '{org}', '{register}', 'Closed', '2026-10-0{day}T08:00:00Z', '{user}', 0, '2026-10-0{day}T18:00:00Z', '{user}', 0, 0, 0, '{"{}"}')
                """;
            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await using var seed = new NpgsqlCommand($"""
                    INSERT INTO identity.users (id, email, display_name, password_hash, is_platform_admin, is_active, failed_login_count, created_at)
                    VALUES ('{user}', 'z@x.de', 'Z', 'h', false, true, 0, '2026-10-01T08:00:00Z');
                    INSERT INTO tenancy.organizations (id, name, legal_name, street, postal_code, city, country_code, is_wholesaler, is_retailer,
                                                       is_repair_provider, status, created_at, created_by)
                    VALUES ('{org}', 'Z', 'Z GmbH', 'S 1', '10115', 'Berlin', 'DE', false, true, false, 'Verified', '2026-10-01T08:00:00Z', '{user}');
                    INSERT INTO inventory.warehouses (id, organization_id, code, name, is_default, created_at)
                    VALUES ('{warehouse}', '{org}', 'MAIN', 'Haupt', true, '2026-10-01T08:00:00Z');
                    INSERT INTO pos.registers (id, organization_id, name, warehouse_id, next_receipt_number, is_active, created_at, cash_rounding)
                    VALUES ('{a}', '{org}', 'A', '{warehouse}', 1, true, '2026-10-01T08:00:00Z', false),
                           ('{b}', '{org}', 'B', '{warehouse}', 1, true, '2026-10-01T08:00:00Z', false);
                    INSERT INTO pos.cash_sessions (id, organization_id, register_id, status, opened_at, opened_by, opening_cash_minor, closed_at, closed_by,
                                                   expected_cash_minor, counted_cash_minor, difference_minor, summary)
                    VALUES {Closed(s3, a, 3)}, {Closed(s1, a, 1)}, {Closed(s2, a, 2)}, {Closed(sb, b, 2)};
                    INSERT INTO pos.cash_sessions (id, organization_id, register_id, status, opened_at, opened_by, opening_cash_minor)
                    VALUES ('{sOpen}', '{org}', '{a}', 'Open', '2026-10-04T08:00:00Z', '{user}', 0);
                    """, c);
                await seed.ExecuteNonQueryAsync();
            }

            await DatabaseMigrator.MigrateAsync(connection);

            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await using var read = new NpgsqlCommand($"""
                    SELECT string_agg(coalesce(z_number::text, '-'), ',' ORDER BY opened_at) FILTER (WHERE register_id = '{a}'),
                           string_agg(z_number::text, ',') FILTER (WHERE register_id = '{b}'),
                           (SELECT next_z_number FROM pos.registers WHERE id = '{a}'), (SELECT next_z_number FROM pos.registers WHERE id = '{b}')
                    FROM pos.cash_sessions
                    """, c);
                await using var reader = await read.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(("1,2,3,-", "1", 4L, 2L), (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
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
