using FluentValidation;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Documents;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

public sealed record InvoiceCustomerRequest(string Name, string? Company, string Street, string PostalCode, string City, string? CountryCode, string? VatId);

/// <summary>Customer: the invoice recipient; null = the customer attached to the receipt (must have a full address).</summary>
public sealed record CreateInvoiceRequest(InvoiceCustomerRequest? Customer);

public sealed record PosInvoiceDto(Guid Id, Guid ReceiptId, string Number, DateOnly IssueDate, DateTimeOffset IssuedAt, Guid IssuedBy, long TotalNetMinor,
    long TotalVatMinor, long TotalGrossMinor, PosInvoiceDocument Document, string PdfUrl);

public sealed record PosInvoiceResult(PosInvoiceDto Invoice, bool Existing);

public sealed record InvoiceFile(string FileName, byte[] Content);

internal sealed class CreateInvoiceValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceValidator() =>
        RuleFor(x => x.Customer!).ChildRules(c =>
        {
            c.RuleFor(x => x.Name).NotEmpty().MaximumLength(InvoiceCustomer.MaxNameLength);
            c.RuleFor(x => x.Company).MaximumLength(InvoiceCustomer.MaxNameLength);
            c.RuleFor(x => x.Street).NotEmpty().MaximumLength(200);
            c.RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(10);
            c.RuleFor(x => x.City).NotEmpty().MaximumLength(100);
            c.RuleFor(x => x.CountryCode).Length(2).When(x => !string.IsNullOrEmpty(x.CountryCode));
            c.RuleFor(x => x.VatId).MaximumLength(20);
        }).When(x => x.Customer is not null);
}

/// <summary>
/// Invoices for POS sales: one per sale receipt, numbered gapless per organization and year (counter row locked until
/// commit, enforced by triggers), immutable, PDF rendered from the stored document. Issuing again returns the invoice.
/// </summary>
internal sealed class PosInvoiceService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IOrganizationDirectory organizations,
    IOrganizationBranding branding, IClock clock)
{
    private static readonly TimeZoneInfo Berlin = FindBerlin();

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<PosInvoiceResult>> IssueAsync(Guid receiptId, CreateInvoiceRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One invoice per receipt: concurrent requests for the same receipt queue here.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"invoice:" + receiptId}, 0))", ct);
        var receipt = await db.Set<Receipt>().AsNoTracking().Include(r => r.Lines).Include(r => r.Payments).AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == receiptId && r.OrganizationId == OrganizationId, ct);
        if (receipt is null)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        if (await db.Set<PosInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.ReceiptId == receiptId, ct) is { } existing)
            return new PosInvoiceResult(ToDto(existing), Existing: true);

        var customer = await CustomerAsync(receipt, request.Customer, ct);
        if (!customer.IsSuccess)
            return customer.Error!;
        var refunded = await db.Set<Receipt>().AnyAsync(r => r.ReferencedReceiptId == receiptId, ct);
        var legal = await organizations.GetLegalDetailsAsync(OrganizationId, ct) ?? throw new InvalidOperationException("Organization not found.");
        var seller = new SellerInfo(legal.Name, legal.LegalName, legal.Street, legal.PostalCode, legal.City, legal.CountryCode, legal.VatId);
        var registerName = await db.Set<Register>().AsNoTracking().Where(r => r.Id == receipt.RegisterId).Select(r => r.Name).FirstAsync(ct);

        var now = clock.UtcNow;
        var issueDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Berlin).DateTime);
        var supplyDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receipt.CreatedAt, Berlin).DateTime);
        // Check everything before a number is taken, so a refusal never touches the counter.
        var probe = PosInvoice.Issue(receipt, refunded, seller, customer.Value, registerName, issueDate.Year, 1, issueDate, supplyDate, actor.UserId, now);
        if (!probe.IsSuccess)
            return probe.Error!;

        var sequence = await AllocateNumberAsync(issueDate.Year, ct);
        var invoice = PosInvoice.Issue(receipt, refunded, seller, customer.Value, registerName, issueDate.Year, sequence, issueDate, supplyDate,
            actor.UserId, now);
        if (!invoice.IsSuccess)
            return invoice.Error!;
        db.Add(invoice.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PosInvoiceResult(ToDto(invoice.Value), Existing: false);
    }

    public async Task<PosInvoiceDto?> ForReceiptAsync(Guid receiptId, CancellationToken ct) =>
        await db.Set<PosInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.ReceiptId == receiptId && i.OrganizationId == OrganizationId, ct) is { } i
            ? ToDto(i)
            : null;

    public async Task<Result<InvoiceFile>> PdfAsync(Guid invoiceId, CancellationToken ct)
    {
        var invoice = await db.Set<PosInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == OrganizationId, ct);
        if (invoice is null)
            return Error.NotFound("pos.invoice_not_found", "Invoice not found.");
        var letterhead = (await branding.GetDocumentBrandingAsync(OrganizationId, ct)).Letterhead;
        return new InvoiceFile($"{invoice.Number}.pdf", PosInvoicePdf.Render(invoice.Document, letterhead));
    }

    /// <summary>Takes the next number of the year: INSERT … ON CONFLICT DO UPDATE +1 RETURNING, row-locked until commit.</summary>
    private async Task<long> AllocateNumberAsync(int year, CancellationToken ct) =>
        (await db.Database.SqlQuery<long>($"""
            INSERT INTO pos.invoice_sequences (organization_id, year, next_number) VALUES ({OrganizationId}, {year}, 2)
            ON CONFLICT (organization_id, year) DO UPDATE SET next_number = pos.invoice_sequences.next_number + 1
            RETURNING next_number - 1 AS "Value"
            """).ToListAsync(ct)).Single();

    private async Task<Result<InvoiceCustomer>> CustomerAsync(Receipt receipt, InvoiceCustomerRequest? given, CancellationToken ct)
    {
        if (given is not null)
            return InvoiceCustomer.Create(given.Name, given.Company, given.Street, given.PostalCode, given.City, given.CountryCode, given.VatId);
        if (receipt.CustomerId is not { } customerId)
            return Error.Validation("pos.invoice_customer_required", "Name and address of the customer are required for an invoice.");
        var c = await db.Set<Customer>().AsNoTracking().FirstAsync(x => x.Id == customerId, ct);
        return InvoiceCustomer.Create(c.Name, c.Company, c.Street, c.PostalCode, c.City, c.CountryCode, c.VatId);
    }

    private static PosInvoiceDto ToDto(PosInvoice i) => new(i.Id, i.ReceiptId, i.Number, i.IssueDate, i.IssuedAt, i.IssuedBy, i.TotalNetMinor,
        i.TotalVatMinor, i.TotalGrossMinor, i.Document, $"/api/pos/invoices/{i.Id}/pdf");

    private static TimeZoneInfo FindBerlin()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

/// <summary>Lays out a <see cref="PosInvoiceDocument"/> on A4 pages (shared SimplePdf writer).</summary>
internal static class PosInvoicePdf
{
    private const double LabelWidth = 150;

    /// <param name="letterhead">Logo and footer lines of the issuing company (its current company profile, ADR-0014).</param>
    public static byte[] Render(PosInvoiceDocument d, DocumentLetterhead? letterhead = null)
    {
        letterhead ??= DocumentLetterhead.None;
        var e = PosInvoice.Euro;
        var seller = d.Seller;
        var sellerLine = string.Join(" · ", new[] { seller.LegalName, seller.Street, $"{seller.PostalCode} {seller.City}" });
        var pdf = new SimplePdf { Title = $"{d.Title} {d.Number}", Footer = $"{d.Title} {d.Number} · {seller.LegalName}",
            FooterLines = letterhead.FooterLines };
        letterhead.ApplyLogo(pdf);

        pdf.Text(sellerLine, 8).Space(14);
        if (d.Customer.Company is { } company)
            pdf.Text(company, 10);
        pdf.Text(d.Customer.Name, 10).Text(d.Customer.Street, 10)
            .Text($"{d.Customer.PostalCode} {d.Customer.City}" + (d.Customer.CountryCode == "DE" ? "" : $", {d.Customer.CountryCode}"), 10);
        pdf.Space(18).Text(d.Title, 18, PdfFont.Bold).Space(4);
        pdf.Field("Rechnungsnummer", d.Number, LabelWidth)
            .Field("Rechnungsdatum", PosInvoice.Date(d.IssueDate), LabelWidth)
            .Field("Leistungsdatum", PosInvoice.Date(d.SupplyDate), LabelWidth)
            .Field("Kassenbeleg", $"Nr. {d.ReceiptNumber} ({d.RegisterName})", LabelWidth);
        if (d.Customer.VatId is { } customerVat)
            pdf.Field("USt-IdNr. Empfänger", customerVat, LabelWidth);
        pdf.Space(10);

        (double X, string Text)[] Row(string a, string b, string c, string f, string g, string h) => [(0, a), (25, b), (275, c), (310, f), (380, g), (465, h)];
        pdf.Columns(9, PdfFont.Bold, Row("Pos.", "Bezeichnung", "Menge", "Einzelpreis", "Rabatt", "Betrag")).Rule();
        foreach (var l in d.Lines)
        {
            pdf.Columns(9, PdfFont.Regular, Row(l.Position.ToString(), Shorten(l.Description, 48), l.Quantity.ToString(), e(l.UnitPriceMinor),
                l.DiscountMinor == 0 ? "" : e(-l.DiscountMinor), $"{e(l.LineGrossMinor)} {l.TaxLabel}"));
            if (l.Imei is not null)
                pdf.Text($"IMEI {l.Imei}", 8, indent: 25);
            if (l.Note is not null)
                pdf.Text(l.Note, 8, indent: 25);
        }
        pdf.Rule().Space(4);
        foreach (var v in d.Vat)
        {
            var rate = $"{v.RateBasisPoints / 100m:0.##} %".Replace('.', ',');
            pdf.Field($"Netto (A, {rate})", e(v.NetMinor), LabelWidth).Field($"Umsatzsteuer {rate}", e(v.VatMinor), LabelWidth);
        }
        if (d.MarginGrossMinor > 0)
            pdf.Field("Differenzbesteuert (§25a)", e(d.MarginGrossMinor), LabelWidth);
        pdf.Field("Gesamtbetrag", e(d.TotalGrossMinor), LabelWidth).Space(8);
        foreach (var p in d.PaymentLines)
            pdf.Text(p, 9);
        pdf.Space(8);
        foreach (var n in d.Notes)
            pdf.Text(n, 9).Space(3);
        pdf.Space(8).Text(seller.LegalName, 8).Text($"{seller.Street}, {seller.PostalCode} {seller.City}", 8);
        if (seller.VatId is { } vatId)
            pdf.Text($"USt-IdNr.: {vatId}", 8);
        return pdf.ToBytes();
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
