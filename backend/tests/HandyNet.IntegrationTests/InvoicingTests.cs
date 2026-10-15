using System.Net;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Invoicing.Application;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Commission invoices on real PostgreSQL. Payments are paid today and then moved into a past month (owner connection),
/// so that each test works on its own completed month and does not see other tests' payments.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class InvoicingTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private async Task<object?> OwnerAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Places and pays an order of <paramref name="quantity"/> × 89,90 €; returns the order id.</summary>
    private async Task<string> PaidOrderAsync(ApiClient shop, Guid offerId, int quantity)
    {
        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() })).Expect(HttpStatusCode.Created);
        var orderId = order.Json["id"]!.GetValue<string>();
        var payment = (await shop.PostAsync($"/api/orders/{orderId}/payments")).Expect(HttpStatusCode.OK);
        var checkout = new Uri(payment.Json["checkoutUrl"]!.GetValue<string>());
        var http = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await http.PostAsync(checkout.AbsolutePath + "/complete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["outcome"] = "Paid" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return orderId;
    }

    private Task MovePaidAtAsync(string orderId, string timestamp) =>
        OwnerAsync($"UPDATE payments.payments SET paid_at = '{timestamp}' WHERE order_id = '{orderId}' AND status = 'Paid'");

    private async Task<(ApiClient Seller, Guid OfferId)> SellerAsync(ApiClient shop)
    {
        var seller = await _scenario.RegisterAsync("grosshandel", wholesaler: true);
        await _scenario.ApproveAsync(seller, "ProductImport", "Inventory", "B2BSales");
        var (offerId, _) = await _scenario.PublishOfferAsync(seller, shop, quantity: 20);
        return (seller, offerId);
    }

    private async Task<ApiClient> ShopAsync()
    {
        var shop = await _scenario.RegisterAsync("handyshop", wholesaler: false);
        await _scenario.ApproveAsync(shop, "B2BPurchasing", "Inventory");
        return shop;
    }

    private static async Task<JsonNode> IssueAsync(ApiClient admin, string month) =>
        (await admin.PostAsync("/api/admin/invoices/issue", new { month })).Expect(HttpStatusCode.OK).Json;

    private static async Task<JsonArray> OwnInvoicesAsync(ApiClient seller) =>
        (await seller.GetAsync("/api/invoices")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray();

    private static long Long(JsonNode node, string name) => node[name]!.GetValue<long>();

    [Fact]
    public async Task Monthly_invoices_per_seller_with_gapless_numbers_idempotent_reruns_and_a_credit_note_after_refund()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await ShopAsync();
        var (sellerA, offerA) = await SellerAsync(shop);
        var (sellerB, offerB) = await SellerAsync(shop);

        // A: 3 × 89,90 (net 269,70 → commission 8,09 + VAT 1,54) and 1 × 89,90 (2,70 + 0,51); B: 2 × 89,90 (5,39 + 1,02).
        var a1 = await PaidOrderAsync(shop, offerA, 3);
        var a2 = await PaidOrderAsync(shop, offerA, 1);
        var b1 = await PaidOrderAsync(shop, offerB, 2);
        await MovePaidAtAsync(a1, "2024-01-10 09:00:00+00");
        await MovePaidAtAsync(a2, "2024-01-31 22:30:00+00"); // 23:30 in Germany: still January
        await MovePaidAtAsync(b1, "2024-01-01 00:30:00+01");

        // Month validation.
        Assert.Equal("validation.failed", (await admin.PostAsync("/api/admin/invoices/issue", new { month = "2024-1" })).Code);
        Assert.Equal("invoicing.month_not_over", (await admin.PostAsync("/api/admin/invoices/issue", new { month = "2099-01" })).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await sellerA.PostAsync("/api/admin/invoices/issue", new { month = "2024-01" })).Status);

        var run = await IssueAsync(admin, "2024-01");
        Assert.Empty(run["failures"]!.AsArray());
        Assert.Equal(2, run["invoicesIssued"]!.GetValue<int>());
        Assert.Equal(0, run["creditNotesIssued"]!.GetValue<int>());

        var listA = await OwnInvoicesAsync(sellerA);
        var invoiceA = Assert.Single(listA)!;
        Assert.Equal("Invoice", invoiceA["kind"]!.GetValue<string>());
        Assert.Equal("2024-01", invoiceA["billingMonth"]!.GetValue<string>());
        var detailA = (await sellerA.GetAsync($"/api/invoices/{invoiceA["id"]}")).Expect(HttpStatusCode.OK).Json;
        var lines = detailA["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.Equal([809L, 270L], lines.Select(l => Long(l!, "netMinor")));
        Assert.Equal([154L, 51L], lines.Select(l => Long(l!, "vatMinor")));
        Assert.Equal((1079L, 205L, 1284L), (Long(detailA, "totalNetMinor"), Long(detailA, "totalVatMinor"), Long(detailA, "totalGrossMinor")));
        Assert.Equal(1900, detailA["vatRateBasisPoints"]!.GetValue<int>());
        Assert.Equal("2024-01-01", detailA["servicePeriodStart"]!.GetValue<string>());
        Assert.Equal("2024-01-31", detailA["servicePeriodEnd"]!.GetValue<string>());
        Assert.StartsWith("grosshandel ", detailA["recipient"]!["name"]!.GetValue<string>());
        Assert.StartsWith("PLATZHALTER", detailA["issuer"]!["name"]!.GetValue<string>()); // placeholders allowed outside Production
        Assert.Matches(@"^HN-\d{4}-\d{6}$", detailA["number"]!.GetValue<string>());

        var invoiceB = Assert.Single(await OwnInvoicesAsync(sellerB))!;
        Assert.Equal((539L, 102L, 641L), (Long(invoiceB, "totalNetMinor"), Long(invoiceB, "totalVatMinor"), Long(invoiceB, "totalGrossMinor")));

        // Re-running the month issues nothing new.
        var rerun = await IssueAsync(admin, "2024-01");
        Assert.Equal(0, rerun["invoicesIssued"]!.GetValue<int>());
        Assert.Equal(2, rerun["alreadyIssued"]!.GetValue<int>());
        Assert.Single(await OwnInvoicesAsync(sellerA));

        // Gapless numbering across the platform: 1..n for the year, counter at n + 1.
        var gaps = await OwnerAsync("""
            SELECT count(*) FROM (SELECT sequence_number, row_number() OVER (ORDER BY sequence_number) AS n
                                  FROM invoicing.invoices WHERE kind = 'Invoice' AND number_year = extract(year FROM now())) s
            WHERE s.sequence_number <> s.n
            """);
        Assert.Equal(0L, gaps);
        Assert.Equal(true, await OwnerAsync("""
            SELECT (SELECT next_number FROM invoicing.number_sequences WHERE kind = 'Invoice' AND year = extract(year FROM now()))
                 = (SELECT count(*) + 1 FROM invoicing.invoices WHERE kind = 'Invoice' AND number_year = extract(year FROM now()))
            """));

        // Seller A cannot see seller B's invoice (not revealed).
        var foreign = await sellerA.GetAsync($"/api/invoices/{invoiceB["id"]}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("invoicing.not_found", foreign.Code);
        Assert.Equal(HttpStatusCode.NotFound, (await sellerA.GetFileAsync($"/api/invoices/{invoiceB["id"]}/pdf")).Status);
        // The buying shop has no B2BSales module.
        Assert.Equal("module.disabled", (await shop.GetAsync("/api/invoices")).Code);

        // Refund after invoicing: the buyer cancels order a2; the refund is in February → credit note in the February run.
        (await shop.PostAsync($"/api/orders/{a2}/cancel", new { reason = "Doch nicht" })).Expect(HttpStatusCode.OK);
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((string?)await OwnerAsync($"SELECT status FROM payments.payments WHERE order_id = '{a2}'") != "Refunded")
        {
            Assert.True(DateTime.UtcNow < deadline, "Refund not processed within 30 s.");
            await outbox.DispatchBatchAsync(CancellationToken.None);
            await Task.Delay(100);
        }
        await OwnerAsync($"UPDATE payments.payments SET refunded_at = '2024-02-05 10:00:00+00' WHERE order_id = '{a2}'");

        var february = await IssueAsync(admin, "2024-02");
        Assert.Empty(february["failures"]!.AsArray());
        Assert.Equal(0, february["invoicesIssued"]!.GetValue<int>());
        Assert.Equal(1, february["creditNotesIssued"]!.GetValue<int>());
        Assert.Equal(0, (await IssueAsync(admin, "2024-02"))["creditNotesIssued"]!.GetValue<int>());

        var note = (await OwnInvoicesAsync(sellerA)).Single(i => i!["kind"]!.GetValue<string>() == "CreditNote")!;
        Assert.Matches(@"^HN-RK-\d{4}-\d{6}$", note["number"]!.GetValue<string>());
        Assert.Equal(invoiceA["id"]!.GetValue<string>(), note["referencedInvoiceId"]!.GetValue<string>());
        Assert.Equal(invoiceA["number"]!.GetValue<string>(), note["referencedInvoiceNumber"]!.GetValue<string>());
        Assert.Equal("2024-02", note["billingMonth"]!.GetValue<string>());
        Assert.Equal((-270L, -51L, -321L), (Long(note, "totalNetMinor"), Long(note, "totalVatMinor"), Long(note, "totalGrossMinor")));
        // The issued invoice itself is unchanged.
        var after = (await sellerA.GetAsync($"/api/invoices/{invoiceA["id"]}")).Json;
        Assert.Equal(1284L, Long(after, "totalGrossMinor"));

        // Admin overview per month (across sellers).
        var january = (await admin.GetAsync("/api/admin/invoices?month=2024-01")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(2, january.Count);
        Assert.Contains(january, d => d!["organizationId"]!.GetValue<string>() == sellerB.OrganizationId.ToString());
        Assert.Single((await admin.GetAsync("/api/admin/invoices?month=2024-02")).Json.AsArray());
        Assert.Equal("invoicing.month", (await admin.GetAsync("/api/admin/invoices?month=xyz")).Code);
    }

    [Fact]
    public async Task Invoices_are_tenant_isolated_immutable_even_for_the_owner_and_the_pdf_has_the_invoice_fields()
    {
        var admin = await _scenario.AdminAsync();
        var shop = await ShopAsync();
        var (seller, offer) = await SellerAsync(shop);
        var vatId = "DE" + Random.Shared.NextInt64(100_000_000, 999_999_999);
        await OwnerAsync($"UPDATE tenancy.organizations SET vat_id = '{vatId}' WHERE id = '{seller.OrganizationId}'");
        var order = await PaidOrderAsync(shop, offer, 3);
        await MovePaidAtAsync(order, "2024-03-10 12:00:00+00");
        Assert.Equal(1, (await IssueAsync(admin, "2024-03"))["invoicesIssued"]!.GetValue<int>());
        var invoice = Assert.Single(await OwnInvoicesAsync(seller))!;
        var id = invoice["id"]!.GetValue<string>();
        var number = invoice["number"]!.GetValue<string>();

        // PDF
        var (status, mediaType, fileName, body) = await seller.GetFileAsync($"/api/invoices/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/pdf", mediaType);
        Assert.Equal($"{number}.pdf", fileName);
        var lines = PdfText.Lines(body);
        Assert.Contains("Rechnung", lines);
        Assert.Contains(number, lines);
        Assert.Contains("01.03.2024 – 31.03.2024", lines);
        Assert.Contains("8,09 €", lines);
        Assert.Contains("1,54 €", lines);
        Assert.Contains("9,63 €", lines);
        Assert.Contains("USt-IdNr.: PLATZHALTER-USt-IdNr", lines);
        Assert.Contains(vatId, lines);
        Assert.Contains(lines, l => l.StartsWith("MUSTER", StringComparison.Ordinal));

        // RLS: another seller's session sees none of it; no tenant sees nothing.
        var other = await _scenario.RegisterAsync("grosshandel", wholesaler: true);
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        async Task<object?> AsApp(Guid? org, string sql)
        {
            await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_org', '{org?.ToString() ?? string.Empty}', false)", app))
                await set.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(sql, app);
            return await command.ExecuteScalarAsync();
        }
        Assert.Equal(1L, await AsApp(seller.OrganizationId, $"SELECT count(*) FROM invoicing.invoices WHERE id = '{id}'"));
        Assert.Equal(0L, await AsApp(other.OrganizationId, $"SELECT count(*) FROM invoicing.invoices WHERE id = '{id}'"));
        Assert.Equal(0L, await AsApp(other.OrganizationId, $"SELECT count(*) FROM invoicing.invoice_lines WHERE invoice_id = '{id}'"));
        Assert.Equal(0L, await AsApp(null, "SELECT count(*) FROM invoicing.invoices"));

        // The app role cannot change, delete or forge documents.
        foreach (var sql in new[]
                 {
                     $"UPDATE invoicing.invoices SET total_net_minor = 1 WHERE id = '{id}'",
                     $"DELETE FROM invoicing.invoice_lines WHERE invoice_id = '{id}'",
                     "DELETE FROM invoicing.number_sequences",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => AsApp(seller.OrganizationId, sql));
            Assert.Equal("42501", ex.SqlState);
        }
        var forged = await Assert.ThrowsAsync<PostgresException>(() => AsApp(seller.OrganizationId, $"""
            INSERT INTO invoicing.invoices (id, organization_id, kind, number, number_year, sequence_number, billing_month, service_period_start,
                service_period_end, issue_date, issued_at, currency, vat_rate_basis_points, total_net_minor, total_vat_minor, total_gross_minor, issuer, recipient)
            VALUES (gen_random_uuid(), '{seller.OrganizationId}', 'Invoice', 'HN-2026-999999', 2026, 999999, '2024-04-01', '2024-04-01', '2024-04-30',
                '2026-01-01', now(), 'EUR', 1900, 100, 19, 119, '[]', '[]')
            """));
        Assert.Equal("23514", forged.SqlState); // number was not allocated

        // Even the owner role cannot edit or delete issued documents.
        foreach (var sql in new[]
                 {
                     $"UPDATE invoicing.invoices SET total_net_minor = total_net_minor WHERE id = '{id}'",
                     $"UPDATE invoicing.invoice_lines SET net_minor = net_minor WHERE invoice_id = '{id}'",
                     $"DELETE FROM invoicing.invoices WHERE id = '{id}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => OwnerAsync(sql));
            Assert.Equal("42501", ex.SqlState);
        }

        // Numbers cannot be skipped: jumping the counter fails at once; taking a number without a document fails at commit.
        var jump = await Assert.ThrowsAsync<PostgresException>(() =>
            OwnerAsync("UPDATE invoicing.number_sequences SET next_number = next_number + 2 WHERE kind = 'Invoice'"));
        Assert.Equal("23514", jump.SqlState);
        var unused = await Assert.ThrowsAsync<PostgresException>(() =>
            OwnerAsync("UPDATE invoicing.number_sequences SET next_number = next_number + 1 WHERE kind = 'Invoice'"));
        Assert.Equal("23514", unused.SqlState);
        Assert.Contains("gap", unused.MessageText);
    }

    [Fact]
    public async Task Monthly_job_issues_last_month_for_each_seller_as_its_system_actor_once()
    {
        var shop = await ShopAsync();
        var (seller, offer) = await SellerAsync(shop);
        var order = await PaidOrderAsync(shop, offer, 1);
        await MovePaidAtAsync(order, "2024-05-20 08:00:00+00");

        var services = fixture.Factory.Services;
        var worker = new InvoiceIssuingWorker(services.GetRequiredService<InvoiceRunner>(), Options.Create(new InvoicingOptions()),
            services.GetRequiredService<IHostEnvironment>(), new FixedClock(new DateTimeOffset(2024, 6, 1, 0, 5, 0, TimeSpan.FromHours(2))),
            NullLogger<InvoiceIssuingWorker>.Instance);

        var first = await worker.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("2024-05", first.Month);
        Assert.Empty(first.Failures);
        var document = Assert.Single(first.Documents, d => d.OrganizationId == seller.OrganizationId);
        Assert.Equal((270L, 51L), (document.TotalNetMinor, document.TotalVatMinor));
        Assert.Null(await worker.RunOnceAsync(CancellationToken.None)); // done for this month in this process

        var own = Assert.Single(await OwnInvoicesAsync(seller))!;
        Assert.Equal(document.Number, own["number"]!.GetValue<string>());
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
