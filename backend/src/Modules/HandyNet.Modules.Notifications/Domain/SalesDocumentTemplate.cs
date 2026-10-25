using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using HandyNet.Modules.Pos.Contracts;

namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// Quotation / invoice email (German, like the document): short text + HTML alternative with the PDF attached. Values are
/// HTML-encoded; the body names the seller, number, total and due/validity date — the customer's address is only in the PDF.
/// </summary>
public static class SalesDocumentTemplate
{
    public const string Template = "pos.sales_document";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Date(DateOnly d) => d.ToString("dd.MM.yyyy", German);

    public static RenderedEmail Render(SalesDocumentEmailRequested e)
    {
        var subject = $"{e.Title} {e.Number} – {e.SellerName}";
        var lines = new List<string>
        {
            "Guten Tag,",
            "",
            e.Kind == "Quotation"
                ? $"anbei erhalten Sie unser Angebot {e.Number} vom {Date(e.Date)}."
                : $"anbei erhalten Sie die {e.Title} {e.Number} vom {Date(e.Date)}.",
            $"Betrag: {EmailTemplates.Euro(e.TotalGrossMinor)}",
        };
        if (e.ValidUntil is { } valid)
            lines.Add($"Gültig bis: {Date(valid)}");
        if (e.DueDate is { } due && e.Kind == "Invoice")
            lines.Add($"Zahlbar bis: {Date(due)}");
        lines.AddRange(["", "Das Dokument finden Sie als PDF im Anhang.", "", "Mit freundlichen Grüßen", e.SellerName]);

        var text = string.Join(Environment.NewLine, lines);
        var html = new StringBuilder("<!doctype html><html lang=\"de\"><body style=\"font-family:Arial,sans-serif;font-size:14px;color:#222\">");
        foreach (var line in lines)
            html.Append(line.Length == 0 ? "<br>" : $"<p style=\"margin:0 0 4px\">{Html.Encode(line)}</p>");
        html.Append("</body></html>");
        var pdf = Convert.FromBase64String(e.PdfBase64);
        return new RenderedEmail(subject, text, html.ToString(), new EmailAttachment(e.PdfFileName, "application/pdf", pdf));
    }
}
