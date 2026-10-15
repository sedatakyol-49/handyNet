using HandyNet.Modules.Invoicing.Application;
using HandyNet.Modules.Invoicing.Domain;
using HandyNet.Tests.Support;

namespace HandyNet.UnitTests;

public class InvoicingTests
{
    private static readonly TimeZoneInfo Zone = InvoiceCalendar.GermanTimeZone;
    private static readonly Guid Seller = Guid.Parse("0199e5a1-0000-7000-8000-000000000001");
    private static readonly BillingMonth September = new(2026, 9);
    private static readonly DateTimeOffset IssuedAt = new(2026, 10, 1, 0, 30, 0, TimeSpan.Zero); // 02:30 in Germany

    private static readonly InvoiceParty Issuer = new("HandyNet", "HandyNet GmbH", "Marktplatz 1", "10115", "Berlin", "DE", "DE123456789", "30/123/45678");
    private static readonly InvoiceParty Recipient = new("Großhandel Nord", "Großhandel Nord GmbH", "Hafenstraße 2", "20457", "Hamburg", "DE", "DE987654321", null);

    private static CommissionItem Item(string order, long net, long vat, int day = 10) =>
        new(Guid.NewGuid(), Guid.NewGuid(), order, new DateTimeOffset(2026, 9, day, 10, 0, 0, TimeSpan.Zero), net, vat, "EUR");

    private static InvoiceNumber Number(InvoiceKind kind, long sequence, int year = 2026) => InvoiceNumber.Create(kind, year, sequence).Value;

    private static Invoice IssueInvoice(params CommissionItem[] items) =>
        Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 7), Issuer, Recipient, items, 1900, IssuedAt, Zone).Value;

    // ---- numbers and months -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(InvoiceKind.Invoice, 2026, 1, "HN-2026-000001")]
    [InlineData(InvoiceKind.Invoice, 2027, 123456, "HN-2027-123456")]
    [InlineData(InvoiceKind.CreditNote, 2026, 42, "HN-RK-2026-000042")]
    public void Numbers_are_formatted_per_series_and_year(InvoiceKind kind, int year, long sequence, string expected) =>
        Assert.Equal(expected, InvoiceNumber.Create(kind, year, sequence).Value.Text);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void Sequence_outside_six_digits_is_rejected(long sequence) =>
        Assert.Equal("invoicing.number_range", InvoiceNumber.Create(InvoiceKind.Invoice, 2026, sequence).Error!.Code);

    [Theory]
    [InlineData("2026-09", 2026, 9)]
    [InlineData(" 2026-12 ", 2026, 12)]
    public void Month_is_parsed(string value, int year, int month) => Assert.Equal(new BillingMonth(year, month), BillingMonth.Parse(value).Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-9")]
    [InlineData("09/2026")]
    [InlineData("1999-01")]
    public void Invalid_month_is_rejected(string? value) => Assert.Equal("invoicing.month", BillingMonth.Parse(value).Error!.Code);

    [Fact]
    public void Month_boundaries_are_german_local_midnight_across_daylight_saving()
    {
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero), September.Start(Zone)); // CEST
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), September.End(Zone));
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), new BillingMonth(2026, 10).End(Zone)); // back to CET
        Assert.Equal(new DateOnly(2026, 9, 30), September.LastDay);
        Assert.Equal(new BillingMonth(2025, 12), new BillingMonth(2026, 1).Previous);
    }

    [Fact]
    public void Previous_month_and_completion_follow_german_time()
    {
        // 30 Sep 23:30 UTC is already 1 Oct 01:30 in Germany.
        var justAfter = new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(September, BillingMonth.PreviousOf(justAfter, Zone));
        Assert.True(September.IsOver(justAfter, Zone));
        Assert.False(September.IsOver(new DateTimeOffset(2026, 9, 30, 21, 59, 0, TimeSpan.Zero), Zone));
    }

    // ---- invoice math -----------------------------------------------------------------------------------------------

    [Fact]
    public void Invoice_has_one_line_per_order_and_totals_are_the_sum_of_the_lines()
    {
        var invoice = IssueInvoice(Item("B2B-2", 809, 154, day: 20), Item("B2B-1", 2_697, 512, day: 3), Item("B2B-3", 1, 0, day: 25));

        Assert.Equal(["B2B-1", "B2B-2", "B2B-3"], invoice.Lines.Select(l => l.OrderNumber)); // ordered by payment time
        Assert.Equal([1, 2, 3], invoice.Lines.Select(l => l.LineNumber));
        Assert.Equal(3_507, invoice.TotalNetMinor);
        Assert.Equal(666, invoice.TotalVatMinor);
        Assert.Equal(4_173, invoice.TotalGrossMinor);
        Assert.All(invoice.Lines, l => Assert.Equal(l.NetMinor + l.VatMinor, l.GrossMinor));
        Assert.Equal("HN-2026-000007", invoice.Number);
        Assert.Equal(new DateOnly(2026, 10, 1), invoice.IssueDate);
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), (invoice.ServicePeriodStart, invoice.ServicePeriodEnd));
        Assert.Equal(new DateOnly(2026, 9, 1), invoice.BillingMonth);
        Assert.Equal(Seller, invoice.OrganizationId);
    }

    [Theory]
    [InlineData(809, 154)]     // 8,09 € → 1,5371 → 1,54
    [InlineData(1_050, 200)]   // 10,50 € → 1,995 → 2,00 (half away from zero)
    [InlineData(1, 0)]
    public void Line_vat_is_the_rate_on_the_net_rounded_half_away_from_zero(long net, long vat) =>
        Assert.Equal(vat, Invoice.Vat(net, 1900));

    [Fact]
    public void Vat_that_does_not_match_the_rate_is_refused()
    {
        var result = Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 1), Issuer, Recipient,
            [Item("B2B-1", 809, 153)], 1900, IssuedAt, Zone);
        Assert.Equal("invoicing.vat_mismatch", result.Error!.Code);
    }

    [Fact]
    public void Payments_outside_the_month_duplicates_and_empty_months_are_refused()
    {
        var october = new CommissionItem(Guid.NewGuid(), Guid.NewGuid(), "B2B-9", new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), 100, 19, "EUR");
        Assert.Equal("invoicing.outside_period", Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 1), Issuer,
            Recipient, [october], 1900, IssuedAt, Zone).Error!.Code);

        var item = Item("B2B-1", 100, 19);
        Assert.Equal("invoicing.duplicate_payment", Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 1), Issuer,
            Recipient, [item, item], 1900, IssuedAt, Zone).Error!.Code);

        Assert.Equal("invoicing.nothing_to_invoice", Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 1), Issuer,
            Recipient, [Item("B2B-0", 0, 0)], 1900, IssuedAt, Zone).Error!.Code);
    }

    [Fact]
    public void Number_must_belong_to_the_year_of_the_issue_date()
    {
        var result = Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 1, year: 2025), Issuer, Recipient,
            [Item("B2B-1", 100, 19)], 1900, IssuedAt, Zone);
        Assert.Equal("invoicing.number_year", result.Error!.Code);
    }

    // ---- credit notes -----------------------------------------------------------------------------------------------

    [Fact]
    public void Credit_note_exactly_reverses_the_refunded_lines_and_references_the_invoice()
    {
        var refunded = Item("B2B-2", 809, 154, day: 20);
        var invoice = IssueInvoice(Item("B2B-1", 2_697, 512, day: 3), refunded);

        var note = Invoice.IssueCreditNote(invoice, [refunded.PaymentId], new BillingMonth(2026, 10), Number(InvoiceKind.CreditNote, 1),
            Issuer, Recipient, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero), Zone).Value;

        Assert.Equal(InvoiceKind.CreditNote, note.Kind);
        Assert.Equal("HN-RK-2026-000001", note.Number);
        Assert.Equal(invoice.Id, note.ReferencedInvoiceId);
        Assert.Equal(invoice.Number, note.ReferencedInvoiceNumber);
        Assert.Equal(invoice.IssueDate, note.ReferencedInvoiceDate);
        Assert.Equal((invoice.ServicePeriodStart, invoice.ServicePeriodEnd), (note.ServicePeriodStart, note.ServicePeriodEnd));
        Assert.Equal(new DateOnly(2026, 10, 1), note.BillingMonth);
        var line = Assert.Single(note.Lines);
        Assert.Equal((-809L, -154L, -963L), (line.NetMinor, line.VatMinor, line.GrossMinor));
        Assert.Equal((invoice.Id, 2), (line.CreditedInvoiceId!.Value, line.CreditedLineNumber!.Value));
        Assert.Equal(refunded.PaymentId, line.PaymentId);
        Assert.Equal((-809L, -154L, -963L), (note.TotalNetMinor, note.TotalVatMinor, note.TotalGrossMinor));
        // What remains billed: the order that was not refunded.
        Assert.Equal(2_697 + 512, invoice.TotalGrossMinor + note.TotalGrossMinor);
    }

    [Fact]
    public void Full_refund_cancels_the_invoice_to_zero()
    {
        var a = Item("B2B-1", 2_697, 512);
        var b = Item("B2B-2", 809, 154);
        var invoice = IssueInvoice(a, b);
        var note = Invoice.IssueCreditNote(invoice, [b.PaymentId, a.PaymentId], new BillingMonth(2026, 10), Number(InvoiceKind.CreditNote, 3),
            Issuer, Recipient, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero), Zone).Value;

        Assert.Equal([1, 2], note.Lines.Select(l => l.CreditedLineNumber!.Value)); // in the invoice's order
        Assert.Equal(0, invoice.TotalNetMinor + note.TotalNetMinor);
        Assert.Equal(0, invoice.TotalVatMinor + note.TotalVatMinor);
    }

    [Fact]
    public void Only_payments_of_the_invoice_can_be_credited_and_credit_notes_are_not_corrected_again()
    {
        var invoice = IssueInvoice(Item("B2B-1", 100, 19));
        var later = new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal("invoicing.payment_not_on_invoice", Invoice.IssueCreditNote(invoice, [Guid.NewGuid()], new BillingMonth(2026, 10),
            Number(InvoiceKind.CreditNote, 1), Issuer, Recipient, later, Zone).Error!.Code);
        Assert.Equal("invoicing.nothing_to_credit", Invoice.IssueCreditNote(invoice, [], new BillingMonth(2026, 10),
            Number(InvoiceKind.CreditNote, 1), Issuer, Recipient, later, Zone).Error!.Code);

        var note = Invoice.IssueCreditNote(invoice, [invoice.Lines[0].PaymentId], new BillingMonth(2026, 10), Number(InvoiceKind.CreditNote, 1),
            Issuer, Recipient, later, Zone).Value;
        Assert.Equal("invoicing.not_an_invoice", Invoice.IssueCreditNote(note, [invoice.Lines[0].PaymentId], new BillingMonth(2026, 10),
            Number(InvoiceKind.CreditNote, 2), Issuer, Recipient, later, Zone).Error!.Code);
    }

    // ---- issuer -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Placeholder_issuer_is_refused_in_production_only()
    {
        var placeholder = new IssuerOptions
        {
            Name = "PLATZHALTER HandyNet", Street = "PLATZHALTER Straße 1", PostalCode = "00000", City = "PLATZHALTER Stadt",
            VatId = "PLATZHALTER-USt-IdNr",
        }.ToParty();
        Assert.True(placeholder.IsPlaceholder);
        Assert.Equal("invoicing.issuer_not_configured", placeholder.ValidateAsIssuer(production: true).Error!.Code);
        Assert.True(placeholder.ValidateAsIssuer(production: false).IsSuccess);
        Assert.True(Issuer.ValidateAsIssuer(production: true).IsSuccess);

        var noTaxId = Issuer with { VatId = null, TaxNumber = " " };
        Assert.Equal("invoicing.issuer_incomplete", noTaxId.ValidateAsIssuer(production: false).Error!.Code);
        Assert.True((Issuer with { VatId = null }).ValidateAsIssuer(production: true).IsSuccess); // tax number alone is enough
    }

    // ---- document -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Document_contains_the_invoice_fields()
    {
        var invoice = IssueInvoice(Item("B2B-1", 2_697, 512, day: 3), Item("B2B-2", 809, 154, day: 20));
        var content = InvoiceDocument.Build(invoice, Zone);
        var text = content.AllText().ToList();

        Assert.Equal("Rechnung", content.Title);
        Assert.Contains("HandyNet GmbH", content.IssuerBlock);
        Assert.Contains("Marktplatz 1", content.IssuerBlock);
        Assert.Contains("10115 Berlin", content.IssuerBlock);
        Assert.Contains("USt-IdNr.: DE123456789", content.IssuerBlock);
        Assert.Contains("Steuernummer: 30/123/45678", content.IssuerBlock);
        Assert.Equal(["Großhandel Nord GmbH", "Großhandel Nord", "Hafenstraße 2", "20457 Hamburg"], content.RecipientBlock);
        var header = content.Header.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("HN-2026-000007", header["Rechnungsnummer"]);
        Assert.Equal("01.10.2026", header["Rechnungsdatum"]);
        Assert.Equal("01.09.2026 – 30.09.2026", header["Leistungszeitraum"]);
        Assert.Equal("DE987654321", header["USt-IdNr. Empfänger"]);
        var totals = content.Totals.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("35,06 €", totals["Summe netto"]);
        Assert.Equal("6,66 €", totals["Umsatzsteuer 19 %"]);
        Assert.Equal("41,72 €", totals["Gesamtbetrag brutto"]);
        Assert.Equal(new DocumentLine("B2B-1", "03.09.2026", "26,97 €", "5,12 €", "32,09 €"), content.Lines[0]);
        Assert.DoesNotContain(text, t => t.Contains("MUSTER"));
    }

    [Fact]
    public void Credit_note_document_names_the_corrected_invoice_and_placeholders_are_marked()
    {
        var placeholderIssuer = Issuer with { Name = "PLATZHALTER HandyNet" };
        var item = Item("B2B-1", 100, 19);
        var invoice = Invoice.IssueCommissionInvoice(Seller, September, Number(InvoiceKind.Invoice, 1), placeholderIssuer, Recipient, [item], 1900,
            IssuedAt, Zone).Value;
        var note = Invoice.IssueCreditNote(invoice, [item.PaymentId], new BillingMonth(2026, 10), Number(InvoiceKind.CreditNote, 1),
            placeholderIssuer, Recipient, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), Zone).Value;

        var content = InvoiceDocument.Build(note, Zone);
        Assert.Equal("Rechnungskorrektur (Storno)", content.Title);
        var header = content.Header.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("HN-RK-2026-000001", header["Korrekturnummer"]);
        Assert.Equal("HN-2026-000001 vom 01.10.2026", header["Korrektur zur Rechnung"]);
        Assert.Equal("-1,19 €", content.Totals.Single(t => t.Label == "Korrekturbetrag brutto").Value);
        Assert.Contains(content.Notes, n => n.StartsWith("MUSTER", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_contains_number_totals_and_vat_ids()
    {
        var invoice = IssueInvoice(Item("B2B-1", 2_697, 512, day: 3));
        var lines = PdfText.Lines(InvoicePdf.Render(InvoiceDocument.Build(invoice, Zone)));
        Assert.Contains("Rechnung", lines);
        Assert.Contains("HN-2026-000007", lines);
        Assert.Contains("32,09 €", lines);
        Assert.Contains("5,12 €", lines);
        Assert.Contains("USt-IdNr.: DE123456789", lines);
        Assert.Contains("DE987654321", lines);
        Assert.Contains("Großhandel Nord GmbH", lines);
    }
}
