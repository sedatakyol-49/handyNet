using HandyNet.Modules.Invoicing.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Documents;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Invoicing.Application;

/// <summary>The seller's own invoices and credit notes (read only; RLS limits them to the tenant as well).</summary>
internal sealed class InvoiceService(HandyNetDbContext db, ITenantContext tenant)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<PagedResult<InvoiceSummaryDto>> ListAsync(int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Invoice>().AsNoTracking().Where(i => i.OrganizationId == OrganizationId);
        var total = await query.CountAsync(ct);
        var rows = await query.Include(i => i.Lines)
            .OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.Number)
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct);
        return new PagedResult<InvoiceSummaryDto>(rows.Select(InvoiceMapping.ToSummary).ToList(), p, size, total);
    }

    public async Task<Result<InvoiceDto>> GetAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, ct) is { } invoice ? InvoiceMapping.ToDto(invoice) : NotFound();

    public async Task<Result<InvoiceFile>> GetPdfAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } invoice)
            return NotFound();
        var content = InvoiceDocument.Build(invoice, InvoiceCalendar.GermanTimeZone);
        return new InvoiceFile($"{invoice.Number}.pdf", InvoicePdf.Render(content));
    }

    private Task<Invoice?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);

    private static Error NotFound() => Error.NotFound("invoicing.not_found", "Invoice not found.");
}

/// <summary>Lays out <see cref="InvoiceDocumentContent"/> on A4 pages.</summary>
internal static class InvoicePdf
{
    private const double LabelWidth = 150;

    public static byte[] Render(InvoiceDocumentContent content)
    {
        var pdf = new SimplePdf { Title = $"{content.Title} {content.Number}", Footer = content.Footer };

        pdf.Text(string.Join(" · ", content.IssuerBlock), 8).Space(14);
        foreach (var line in content.RecipientBlock)
            pdf.Text(line, 10);
        pdf.Space(18).Text(content.Title, 18, PdfFont.Bold).Space(4);
        foreach (var field in content.Header)
            pdf.Field(field.Label, field.Value, LabelWidth);
        pdf.Space(10).Text("Leistung", 11, PdfFont.Bold).Text(content.ServiceDescription).Space(6);

        (double X, string Text)[] Row(string a, string b, string c, string d, string e) => [(0, a), (150, b), (250, c), (335, d), (415, e)];
        pdf.Columns(9, PdfFont.Bold, Row("Bestellung", "Bezahlt am", "Netto", "USt", "Brutto")).Rule();
        foreach (var line in content.Lines)
            pdf.Columns(9, PdfFont.Regular, Row(line.OrderNumber, line.PaidOn, line.Net, line.Vat, line.Gross));
        pdf.Rule().Space(4);
        foreach (var total in content.Totals)
            pdf.Field(total.Label, total.Value, LabelWidth);
        pdf.Space(12);
        foreach (var note in content.Notes)
            pdf.Text(note, 9).Space(3);
        pdf.Space(8);
        foreach (var line in content.IssuerBlock)
            pdf.Text(line, 8);
        return pdf.ToBytes();
    }
}
