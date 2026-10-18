using System.Globalization;
using System.Net;
using System.Text;
using HandyNet.Modules.Pos.Contracts;

namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// E-receipt email (German, like the printed receipt): plain text plus an HTML alternative with the same content. All
/// values are HTML-encoded; the email carries the receipt as issued, no customer data and no tracking.
/// </summary>
public static class EReceiptTemplate
{
    public const string Template = "pos.receipt";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly TimeZoneInfo Berlin = FindBerlin();

    private static string Euro(long minor) => EmailTemplates.Euro(minor);

    private static string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Berlin).ToString("dd.MM.yyyy HH:mm", German);

    private static string Method(string method) => method switch
    {
        "Cash" => "Bar",
        "Card" => "Karte",
        _ => method,
    };

    public static RenderedEmail Render(EReceiptContent c)
    {
        var subject = $"{c.Title} Nr. {c.ReceiptNumber} – {c.SellerName}";

        var text = new StringBuilder();
        text.AppendLine($"{c.SellerName}");
        if (c.SellerLegalName != c.SellerName)
            text.AppendLine(c.SellerLegalName);
        foreach (var line in c.SellerAddress)
            text.AppendLine(line);
        if (!string.IsNullOrWhiteSpace(c.SellerVatId))
            text.AppendLine($"USt-IdNr.: {c.SellerVatId}");
        text.AppendLine();
        text.AppendLine($"{c.Title} Nr. {c.ReceiptNumber} · {c.RegisterName} · {Time(c.IssuedAt)}");
        text.AppendLine();
        foreach (var l in c.Lines)
        {
            text.AppendLine($"{l.Quantity} × {l.Description}  {Euro(l.LineGrossMinor)} {l.TaxLabel}");
            if (l.Imei is not null)
                text.AppendLine($"   IMEI {l.Imei}");
            if (l.DiscountMinor != 0)
                text.AppendLine($"   Rabatt {Euro(-l.DiscountMinor)}");
            if (l.Note is not null)
                text.AppendLine($"   {l.Note}");
        }
        text.AppendLine();
        text.AppendLine($"Summe {Euro(c.TotalGrossMinor)}");
        if (c.RoundingMinor != 0)
            text.AppendLine($"Rundung {Euro(c.RoundingMinor)}");
        foreach (var v in c.Vat)
            text.AppendLine($"{v.Label}: Netto {Euro(v.NetMinor)} · USt {Euro(v.VatMinor)} · Brutto {Euro(v.GrossMinor)}");
        foreach (var p in c.Payments)
            text.AppendLine($"{Method(p.Method)} {Euro(p.TenderedMinor ?? p.AmountMinor)}");
        if (c.ChangeMinor > 0)
            text.AppendLine($"Rückgeld {Euro(c.ChangeMinor)}");
        foreach (var n in c.Notes)
        {
            text.AppendLine();
            text.AppendLine(n);
        }
        text.AppendLine();
        text.AppendLine("Vielen Dank für Ihren Einkauf!");

        string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><title>").Append(E(subject)).Append("</title></head>");
        html.Append("<body style=\"margin:0;padding:24px;background:#f3f6f7;font-family:Arial,Helvetica,sans-serif;color:#17282e\">");
        html.Append("<table role=\"presentation\" style=\"max-width:480px;margin:0 auto;background:#ffffff;border-radius:8px;padding:20px;width:100%\">");
        html.Append("<tr><td><p style=\"margin:0;font-size:16px;font-weight:bold\">").Append(E(c.SellerName)).Append("</p>");
        html.Append("<p style=\"margin:4px 0 0;font-size:12px;color:#55686e\">");
        if (c.SellerLegalName != c.SellerName)
            html.Append(E(c.SellerLegalName)).Append("<br>");
        html.Append(string.Join("<br>", c.SellerAddress.Select(E)));
        if (!string.IsNullOrWhiteSpace(c.SellerVatId))
            html.Append("<br>USt-IdNr.: ").Append(E(c.SellerVatId));
        html.Append("</p><h1 style=\"font-size:18px;margin:16px 0 4px\">").Append(E($"{c.Title} Nr. {c.ReceiptNumber}")).Append("</h1>");
        html.Append("<p style=\"margin:0 0 12px;font-size:12px;color:#55686e\">").Append(E($"{c.RegisterName} · {Time(c.IssuedAt)}")).Append("</p>");
        html.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:collapse;font-size:13px\">");
        foreach (var l in c.Lines)
        {
            html.Append("<tr><td style=\"padding:4px 0;border-top:1px solid #e3e9eb\">").Append(E($"{l.Quantity} × {l.Description}"));
            if (l.Imei is not null)
                html.Append("<br><span style=\"color:#55686e;font-size:11px\">IMEI ").Append(E(l.Imei)).Append("</span>");
            if (l.DiscountMinor != 0)
                html.Append("<br><span style=\"color:#55686e;font-size:11px\">Rabatt ").Append(E(Euro(-l.DiscountMinor))).Append("</span>");
            if (l.Note is not null)
                html.Append("<br><span style=\"color:#55686e;font-size:11px\">").Append(E(l.Note)).Append("</span>");
            html.Append("</td><td style=\"padding:4px 0;border-top:1px solid #e3e9eb;text-align:right;white-space:nowrap\">")
                .Append(E($"{Euro(l.LineGrossMinor)} {l.TaxLabel}")).Append("</td></tr>");
        }
        void Row(string label, string value, bool bold = false) =>
            html.Append("<tr><td style=\"padding:3px 0").Append(bold ? ";font-weight:bold;font-size:15px" : "").Append("\">").Append(E(label))
                .Append("</td><td style=\"padding:3px 0;text-align:right").Append(bold ? ";font-weight:bold;font-size:15px" : "").Append("\">")
                .Append(E(value)).Append("</td></tr>");
        Row("Summe", Euro(c.TotalGrossMinor), bold: true);
        if (c.RoundingMinor != 0)
            Row("Rundung", Euro(c.RoundingMinor));
        foreach (var v in c.Vat)
            Row($"{v.Label} (Netto {Euro(v.NetMinor)})", $"USt {Euro(v.VatMinor)}");
        foreach (var p in c.Payments)
            Row(Method(p.Method), Euro(p.TenderedMinor ?? p.AmountMinor));
        if (c.ChangeMinor > 0)
            Row("Rückgeld", Euro(c.ChangeMinor));
        html.Append("</table>");
        foreach (var n in c.Notes)
            html.Append("<p style=\"margin:10px 0 0;font-size:11px;color:#55686e\">").Append(E(n)).Append("</p>");
        html.Append("<p style=\"margin:16px 0 0;font-size:13px\">Vielen Dank für Ihren Einkauf!</p></td></tr></table></body></html>");

        return new RenderedEmail(subject, text.ToString(), html.ToString());
    }

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
