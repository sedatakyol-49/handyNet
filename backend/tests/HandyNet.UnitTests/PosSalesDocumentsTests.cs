using HandyNet.Modules.Pos.Domain;

namespace HandyNet.UnitTests;

/// <summary>Domain rules of quotations, sales invoices, credit notes, invoice payments and Z numbers (ADR-0015).</summary>
public class PosSalesDocumentsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 22);
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static readonly SellerInfo Seller = new("Handy Shop", "Handy Shop GmbH", "Hauptstraße 1", "10115", "Berlin", "DE", "DE123456789");

    private static readonly SalesCustomer Business = new(null, "Max Muster", "Muster GmbH", "Gewerbering 9", "20095", "Hamburg", "DE", "DE987654321");

    private static SalesLineInput Free(long price, int quantity = 1, long discount = 0, int vat = 1900) =>
        new(null, null, null, "Leistung", null, quantity, price, discount, vat, false);

    private static SalesLineInput Device(long price) => new(Guid.NewGuid(), Guid.NewGuid(), "350000000000001", "Gerät", null, 1, price, 0, 1900, true);

    private static SalesInvoice Draft(params SalesLineInput[] lines) => Draft(Business, PriceMode.Gross, false, lines);

    private static SalesInvoice Draft(SalesCustomer? customer, PriceMode mode, bool reverseCharge, params SalesLineInput[] lines) =>
        SalesInvoice.CreateDraft(Org, new SalesInvoiceDetails(customer, null, 14, mode, reverseCharge, Guid.NewGuid(), lines, null), null, User, Now).Value;

    // ---- VAT and totals ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(119_00, 1900, 19_00)]
    [InlineData(107_00, 700, 7_00)]
    [InlineData(10_00, 0, 0)]
    [InlineData(-119_00, 1900, -19_00)]
    public void Vat_from_gross_at_any_rate(long gross, int rate, long vat) => Assert.Equal(vat, VatCalculator.VatFromGross(gross, rate));

    [Theory]
    [InlineData(100_00, 1900, 19_00)]
    [InlineData(1_05, 700, 7)]   // 7.35 → 7
    [InlineData(50, 1900, 10)]   // 9.5 → 10 (half away from zero)
    [InlineData(-50, 1900, -10)]
    public void Vat_on_net_rounds_half_away_from_zero(long net, int rate, long vat) => Assert.Equal(vat, VatCalculator.VatOnNet(net, rate));

    [Fact]
    public void Gross_mode_takes_vat_out_of_the_sum_per_rate_and_margin_lines_show_none()
    {
        var lines = SalesCalculator.Lines([Free(11_90, quantity: 2), Free(10_70, vat: 700), Device(250_00)]).Value;
        var totals = SalesCalculator.Compute(PriceMode.Gross, lines);
        Assert.Equal([1900, 700], totals.Vat.Select(v => v.RateBasisPoints));
        Assert.Equal((20_00L, 3_80L, 23_80L), (totals.Vat[0].NetMinor, totals.Vat[0].VatMinor, totals.Vat[0].GrossMinor));
        Assert.Equal((10_00L, 70L), (totals.Vat[1].NetMinor, totals.Vat[1].VatMinor));
        Assert.Equal(250_00, totals.MarginGrossMinor);
        Assert.Equal(23_80 + 10_70 + 250_00, totals.TotalGrossMinor);
        Assert.Equal(4_50, totals.TotalVatMinor);
        Assert.Equal(totals.TotalGrossMinor, totals.TotalNetMinor + totals.TotalVatMinor);
    }

    [Fact]
    public void Net_mode_adds_vat_per_rate_and_counts_margin_lines_with_their_final_price()
    {
        var lines = SalesCalculator.Lines([Free(10_00, quantity: 3, discount: 1_00), Free(50_00, vat: 0), Device(100_00)]).Value;
        var totals = SalesCalculator.Compute(PriceMode.Net, lines);
        Assert.Equal((29_00L, 5_51L, 34_51L), (totals.Vat[0].NetMinor, totals.Vat[0].VatMinor, totals.Vat[0].GrossMinor));
        Assert.Equal((50_00L, 0L), (totals.Vat[1].NetMinor, totals.Vat[1].VatMinor));
        Assert.Equal(34_51 + 50_00 + 100_00, totals.TotalGrossMinor);
    }

    [Theory]
    [InlineData(0, 10_00, 0, 1900, "pos.sales_quantity")]
    [InlineData(1, -1, 0, 1900, "pos.sales_unit_price")]
    [InlineData(2, 5_00, 10_01, 1900, "pos.sales_discount")]
    [InlineData(1, 5_00, 0, 1600, "pos.sales_vat_rate")]
    public void Invalid_lines_are_refused(int quantity, long price, long discount, int vat, string code) =>
        Assert.Equal(code, SalesCalculator.Lines([Free(price, quantity, discount, vat)]).Error?.Code);

    [Fact]
    public void Margin_and_device_lines_need_a_single_serialized_unit()
    {
        Assert.Equal("pos.margin_line", SalesCalculator.Lines([new SalesLineInput(Guid.NewGuid(), null, null, "x", null, 1, 1, 0, 1900, true)]).Error?.Code);
        Assert.Equal("pos.sales_unit_line",
            SalesCalculator.Lines([new SalesLineInput(Guid.NewGuid(), Guid.NewGuid(), "1", "x", null, 2, 1, 0, 1900, false)]).Error?.Code);
        var unit = Guid.NewGuid();
        Assert.Equal("pos.duplicate_unit", SalesCalculator.Lines([
            new SalesLineInput(Guid.NewGuid(), unit, "1", "x", null, 1, 1, 0, 1900, false), new SalesLineInput(Guid.NewGuid(), unit, "1", "x", null, 1, 1, 0, 1900, false),
        ]).Error?.Code);
    }

    [Theory]
    [InlineData(0, 100, "Open")]
    [InlineData(40, 100, "PartiallyPaid")]
    [InlineData(100, 100, "Paid")]
    public void Payment_state_follows_the_paid_amount(long paid, long total, string state) =>
        Assert.Equal(state, SalesCalculator.StateOf(total, paid).ToString());

    // ---- quotations --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Quotation_numbers_status_rules_and_expiry()
    {
        var q = Quotation.Create(Org, 2026, 7, Today, new QuotationDetails(Business, Today.AddDays(30), PriceMode.Gross, [Free(10_00)], "Hinweis"), User, Now).Value;
        Assert.Equal("AN-2026-0007", q.Number);
        Assert.Equal("AN-2026-12345", Quotation.FormatNumber(2026, 12345));
        Assert.Equal("Draft", q.DisplayStatus(Today));
        Assert.Equal("Expired", q.DisplayStatus(Today.AddDays(31)));
        Assert.True(q.MarkSent(Now).IsSuccess);
        Assert.Equal(QuotationStatus.Sent, q.Status);
        Assert.Equal("pos.quotation_valid_until",
            q.Update(new QuotationDetails(Business, Today.AddDays(-1), PriceMode.Gross, [Free(1)], null), Now).Error?.Code);
        Assert.True(q.MarkConverted(Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(QuotationStatus.Accepted, q.Status);
        Assert.False(q.IsEditable);
        Assert.Equal("pos.quotation_converted", q.SetStatus(QuotationStatus.Rejected, Now).Error?.Code);
        Assert.Equal("pos.quotation_converted", q.MarkConverted(Guid.NewGuid(), Now).Error?.Code);
        var document = q.ToDocument(Seller);
        Assert.Equal(("Angebot", "AN-2026-0007"), (document.Title, document.Number));
        Assert.Contains(document.Notes, n => n.Contains("gültig bis 21.11.2026"));
    }

    [Fact]
    public void Rejected_quotation_cannot_be_converted_until_reopened()
    {
        var q = Quotation.Create(Org, 2026, 1, Today, new QuotationDetails(Business, Today, PriceMode.Gross, [Free(10_00)], null), User, Now).Value;
        Assert.True(q.SetStatus(QuotationStatus.Rejected, Now).IsSuccess);
        Assert.Equal("pos.quotation_rejected", q.MarkConverted(Guid.NewGuid(), Now).Error?.Code);
        Assert.True(q.SetStatus(QuotationStatus.Sent, Now).IsSuccess);
        Assert.True(q.MarkConverted(Guid.NewGuid(), Now).IsSuccess);
    }

    // ---- sales invoices ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Issuing_needs_seller_vat_id_customer_address_and_lines()
    {
        Assert.Equal("pos.invoice_seller_vat_id_missing", Draft(Free(10_00)).CheckIssuable(Seller with { VatId = null }).Error?.Code);
        Assert.Equal("pos.invoice_customer_required", Draft(null, PriceMode.Gross, false, Free(10_00)).CheckIssuable(Seller).Error?.Code);
        Assert.Equal("pos.invoice_customer_address",
            Draft(Business with { Street = null }, PriceMode.Gross, false, Free(10_00)).CheckIssuable(Seller).Error?.Code);
        Assert.Equal("pos.sales_empty", Draft().CheckIssuable(Seller).Error?.Code);
        Assert.Equal("pos.reverse_charge_vat_id", Draft(Business with { VatId = null }, PriceMode.Net, true, Free(10_00, vat: 0)).CheckIssuable(Seller).Error?.Code);
        Assert.True(Draft(Free(10_00)).CheckIssuable(Seller).IsSuccess);
    }

    [Fact]
    public void Reverse_charge_allows_only_zero_rate_lines()
    {
        var details = new SalesInvoiceDetails(Business, null, 14, PriceMode.Net, true, null, [Free(5000_00)], null);
        Assert.Equal("pos.reverse_charge_lines", SalesInvoice.CreateDraft(Org, details, null, User, Now).Error?.Code);
        var ok = Draft(Business, PriceMode.Net, true, Free(5000_00, vat: 0));
        Assert.True(ok.Issue(Seller, 2026, 1, Today, [], User, Now).IsSuccess);
        Assert.Contains(SalesNotes.ReverseChargeNote, ok.Document!.Notes);
        Assert.Equal(0, ok.TotalVatMinor);
    }

    [Fact]
    public void Issued_invoice_has_number_dates_and_a_final_document()
    {
        var invoice = Draft(Free(11_90, quantity: 2), Device(300_00));
        var margin = new MarginDetail(2, Guid.NewGuid(), 220_00, VatCalculator.MarginVat(300_00, 220_00));
        Assert.True(invoice.Issue(Seller, 2026, 42, Today, [margin], User, Now).IsSuccess);
        Assert.Equal("RE-2026-000042", invoice.Number);
        Assert.Equal((Today, Today, Today.AddDays(14)), (invoice.InvoiceDate!.Value, invoice.ServiceDate!.Value, invoice.DueDate!.Value));
        Assert.Contains(SalesNotes.MarginNote, invoice.Document!.Notes);
        Assert.Contains(invoice.Document.Notes, n => n.StartsWith("Zahlbar ohne Abzug bis 05.11.2026"));
        Assert.Equal(12_77, invoice.MarginDetails!.Single().MarginVatMinor);
        Assert.Equal("pos.sales_invoice_issued", invoice.UpdateDraft(invoice.ToDetails(), Now).Error?.Code);
        Assert.Equal("pos.sales_invoice_issued", invoice.Discard(Now).Error?.Code);
        Assert.Equal("pos.sales_invoice_issued", invoice.Issue(Seller, 2026, 43, Today, [], User, Now).Error?.Code);
    }

    [Fact]
    public void Credit_note_is_the_exact_negative_mirror_and_references_the_original()
    {
        var invoice = Draft(Free(10_00, quantity: 3, discount: 50), Free(10_70, vat: 700));
        invoice.Issue(Seller, 2026, 1, Today, [], User, Now);
        Assert.Equal("pos.reason", SalesInvoice.CreditNoteFor(invoice, Seller, 2026, 2, Today, " ", User, Now).Error?.Code);
        var note = SalesInvoice.CreditNoteFor(invoice, Seller, 2026, 2, Today, "Rücknahme", User, Now).Value;
        Assert.Equal((SalesInvoiceKind.CreditNote, "RE-2026-000002", invoice.Id), (note.Kind, note.Number, note.OriginalInvoiceId!.Value));
        Assert.Equal((-invoice.TotalGrossMinor, -invoice.TotalVatMinor, -invoice.TotalNetMinor), (note.TotalGrossMinor, note.TotalVatMinor, note.TotalNetMinor));
        Assert.Equal(invoice.Lines.Select(l => -l.AmountMinor), note.Lines.Select(l => l.AmountMinor));
        Assert.Equal(invoice.Lines.Select(l => -l.Quantity), note.Lines.Select(l => l.Quantity));
        Assert.Equal(("Stornorechnung", "RE-2026-000001"), (note.Document!.Title, note.Document.ReferencedNumber));
        Assert.Equal("pos.sales_invoice_not_issued", SalesInvoice.CreditNoteFor(note, Seller, 2026, 3, Today, "x", User, Now).Error?.Code);
        Assert.Equal("pos.sales_invoice_not_issued", SalesInvoice.CreditNoteFor(Draft(Free(1)), Seller, 2026, 3, Today, "x", User, Now).Error?.Code);
    }

    [Fact]
    public void Payments_are_positive_within_the_open_amount_and_cash_needs_a_receipt()
    {
        var invoice = Draft(Free(100_00));
        Assert.Equal("pos.sales_invoice_not_issued",
            SalesInvoicePayment.Record(invoice, InvoicePaymentMethod.BankTransfer, 1_00, Today, null, 0, false, null, User, Now).Error?.Code);
        invoice.Issue(Seller, 2026, 1, Today, [], User, Now);
        Assert.Equal("pos.payment_exceeds_open",
            SalesInvoicePayment.Record(invoice, InvoicePaymentMethod.BankTransfer, 60_01, Today, null, 40_00, false, null, User, Now).Error?.Code);
        Assert.Equal("pos.sales_invoice_cancelled",
            SalesInvoicePayment.Record(invoice, InvoicePaymentMethod.BankTransfer, 1_00, Today, null, 0, true, null, User, Now).Error?.Code);
        Assert.Equal("pos.cash_payment_at_till",
            SalesInvoicePayment.Record(invoice, InvoicePaymentMethod.Cash, 1_00, Today, null, 0, false, null, User, Now).Error?.Code);
        Assert.True(SalesInvoicePayment.Record(invoice, InvoicePaymentMethod.Cash, 60_00, Today, null, 40_00, false, Guid.NewGuid(), User, Now).IsSuccess);
    }

    // ---- till: invoice payment receipt and Z numbers -------------------------------------------------------------------------

    private static ReceiptHeader Header() => new(Org, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5, User, Now);

    [Fact]
    public void Invoice_payment_receipt_has_one_not_taxable_line_without_product_and_settles_like_a_sale()
    {
        var receipt = Receipt.CreateInvoicePayment(Header(), Guid.NewGuid(), "RE-2026-000003", 60_00, [new PaymentInput(PaymentMethod.Cash, 70_00)]).Value;
        Assert.Equal(ReceiptKind.InvoicePayment, receipt.Kind);
        var line = receipt.Lines.Single();
        Assert.Equal((null as Guid?, TaxTreatment.NotTaxable, 0, 0L), (line.ProductId, line.TaxTreatment, line.VatRateBasisPoints, line.VatMinor));
        Assert.Equal("Zahlung Rechnung RE-2026-000003", line.Description);
        Assert.Equal((60_00L, 0L, 10_00L), (receipt.TotalGrossMinor, receipt.TotalVatMinor, receipt.ChangeMinor));
        Assert.Equal("pos.payment_insufficient",
            Receipt.CreateInvoicePayment(Header(), Guid.NewGuid(), "RE-1", 60_00, [new PaymentInput(PaymentMethod.Cash, 50_00)]).Error?.Code);
        // Not taxable → the 0 % bucket of the TSE process data.
        var summary = SessionCalculator.Summarize(0, [receipt], []);
        Assert.Equal("Beleg^0.00_0.00_0.00_0.00_60.00^60.00:Bar", KassenSichV.ReceiptProcessData(summary.ByTaxTreatment, summary.ByPaymentMethod));
        Assert.Equal((1, 60_00L, 0), (summary.InvoicePaymentCount, summary.InvoicePaymentsMinor, summary.SaleCount));
    }

    [Fact]
    public void Closing_stores_the_z_number()
    {
        var register = Register.Create(Org, "Kasse", Guid.NewGuid(), Now).Value;
        Assert.Equal(1, register.NextZNumber);
        var session = CashSession.Open(Org, register, 0, User, Now).Value;
        var summary = SessionCalculator.Summarize(0, [], []);
        Assert.Equal("pos.z_number", session.Close(summary, 0, null, User, Now, 0).Error?.Code);
        Assert.True(session.Close(summary, 0, null, User, Now, 3).IsSuccess);
        Assert.Equal(3, session.ZNumber);
    }
}
