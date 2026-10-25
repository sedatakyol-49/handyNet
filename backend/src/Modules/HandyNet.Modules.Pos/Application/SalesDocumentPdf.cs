using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel.Documents;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// Lays out a quotation, invoice or credit note on A4 (shared SimplePdf writer), German, like the invoice for a POS sale:
/// sender line, recipient address, title and facts, line table, net/VAT per rate, §25a gross without VAT, total, notes, seller
/// footer. A layout, no legal completeness claimed (to be reviewed by a tax adviser).
/// </summary>
internal static class SalesDocumentPdf
{
    private const double LabelWidth = 160;

    /// <param name="letterhead">Logo and footer lines of the issuing company (current company profile).</param>
    public static byte[] Render(SalesDocument d, DocumentLetterhead? letterhead = null)
    {
        var e = PosInvoice.Euro;
        var seller = d.Seller;
        letterhead ??= DocumentLetterhead.None;
        var pdf = new SimplePdf { Title = $"{d.Title} {d.Number}", Footer = $"{d.Title} {d.Number} · {seller.LegalName}", FooterLines = letterhead.FooterLines };
        letterhead.ApplyLogo(pdf);

        pdf.Text(string.Join(" · ", new[] { seller.LegalName, seller.Street, $"{seller.PostalCode} {seller.City}" }), 8).Space(14);
        var c = d.Customer;
        if (c.Company is { } company)
            pdf.Text(company, 10);
        pdf.Text(c.Name, 10);
        if (c.Street is { } street)
            pdf.Text(street, 10);
        if (c.PostalCode is not null || c.City is not null)
            pdf.Text($"{c.PostalCode} {c.City}".Trim() + (c.CountryCode is null or "DE" ? "" : $", {c.CountryCode}"), 10);

        pdf.Space(18).Text(d.Title, 18, PdfFont.Bold).Space(4);
        var quotation = d.Kind == "Quotation";
        pdf.Field(quotation ? "Angebotsnummer" : "Rechnungsnummer", d.Number, LabelWidth)
            .Field(quotation ? "Angebotsdatum" : "Rechnungsdatum", PosInvoice.Date(d.Date), LabelWidth);
        if (d.ServiceDate is { } service)
            pdf.Field("Leistungsdatum", PosInvoice.Date(service), LabelWidth);
        if (d.ValidUntil is { } valid)
            pdf.Field("Gültig bis", PosInvoice.Date(valid), LabelWidth);
        if (d.DueDate is { } due && d.Kind == "Invoice")
            pdf.Field("Fällig am", PosInvoice.Date(due), LabelWidth);
        if (d.ReferencedNumber is { } referenced)
            pdf.Field("Zu Rechnung", referenced, LabelWidth);
        if (c.VatId is { } customerVat)
            pdf.Field("USt-IdNr. Empfänger", customerVat, LabelWidth);
        pdf.Field("Preise", d.PriceMode == PriceMode.Net ? "netto zzgl. USt" : "brutto inkl. USt", LabelWidth);
        pdf.Space(10);

        (double X, string Text)[] Row(string a, string b, string q, string u, string r, string v, string t) =>
            [(0, a), (25, b), (255, q), (295, u), (365, r), (420, v), (455, t)];
        pdf.Columns(9, PdfFont.Bold, Row("Pos.", "Bezeichnung", "Menge", "Einzelpreis", "Rabatt", "USt", "Betrag")).Rule();
        foreach (var l in d.Lines)
        {
            pdf.Columns(9, PdfFont.Regular, Row(l.Position.ToString(), Shorten(l.Description, 44), l.Quantity.ToString(), e(l.UnitPriceMinor),
                l.DiscountMinor == 0 ? "" : e(-l.DiscountMinor), l.TaxLabel, e(l.AmountMinor)));
            if (l.Imei is not null)
                pdf.Text($"IMEI {l.Imei}", 8, indent: 25);
            if (l.Note is not null)
                pdf.Text(l.Note, 8, indent: 25);
        }
        pdf.Rule().Space(4);

        foreach (var v in d.Vat)
        {
            var rate = $"{v.RateBasisPoints / 100m:0.##} %".Replace('.', ',');
            pdf.Field($"Netto ({rate})", e(v.NetMinor), LabelWidth).Field($"Umsatzsteuer {rate}", e(v.VatMinor), LabelWidth);
        }
        if (d.MarginGrossMinor != 0)
            pdf.Field("Differenzbesteuert (§25a)", e(d.MarginGrossMinor), LabelWidth);
        pdf.Field("Gesamtbetrag", e(d.TotalGrossMinor), LabelWidth).Space(8);

        if (d.FreeText is { } text)
            pdf.Text(text, 9).Space(6);
        foreach (var n in d.Notes)
            pdf.Text(n, 9).Space(3);
        pdf.Space(8).Text(seller.LegalName, 8).Text($"{seller.Street}, {seller.PostalCode} {seller.City}", 8);
        if (seller.VatId is { } vatId)
            pdf.Text($"USt-IdNr.: {vatId}", 8);
        return pdf.ToBytes();
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
