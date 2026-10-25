using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text;
using HandyNet.Modules.Pos.Contracts;

namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// E-receipt email (German by default, like the printed receipt; also in the other UI locales): plain text plus an HTML
/// alternative with the same content. All values are HTML-encoded; the email carries the receipt as issued, no customer
/// data and no tracking. For Arabic and Persian the HTML is right-to-left.
/// </summary>
public static class EReceiptTemplate
{
    public const string Template = "pos.receipt";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly TimeZoneInfo Berlin = FindBerlin();

    /// <summary>Encodes markup characters only; umlauts and other letters stay readable.</summary>
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Euro(long minor) => EmailTemplates.Euro(minor);

    private static string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Berlin).ToString("dd.MM.yyyy HH:mm", German);

    private static string Method(string method, string language) => method switch
    {
        "Cash" => EmailText.Get("receipt.cash", language),
        "Card" => EmailText.Get("receipt.card", language),
        _ => method,
    };

    public static RenderedEmail Render(EReceiptContent c, string language = "de")
    {
        language = EmailText.Normalize(language);
        var rtl = EmailText.IsRtl(language);
        var end = rtl ? "left" : "right";
        string L(string key) => EmailText.Get(key, language);
        var no = L("receipt.number");

        var subject = $"{c.Title} {no} {c.ReceiptNumber} – {c.SellerName}";

        var text = new StringBuilder();
        text.AppendLine($"{c.SellerName}");
        if (c.SellerLegalName != c.SellerName)
            text.AppendLine(c.SellerLegalName);
        foreach (var line in c.SellerAddress)
            text.AppendLine(line);
        if (!string.IsNullOrWhiteSpace(c.SellerVatId))
            text.AppendLine($"{L("receipt.vatid")}: {c.SellerVatId}");
        text.AppendLine();
        text.AppendLine($"{c.Title} {no} {c.ReceiptNumber} · {c.RegisterName} · {Time(c.IssuedAt)}");
        text.AppendLine();
        foreach (var l in c.Lines)
        {
            text.AppendLine($"{l.Quantity} × {l.Description}  {Euro(l.LineGrossMinor)} {l.TaxLabel}");
            if (l.Imei is not null)
                text.AppendLine($"   IMEI {l.Imei}");
            if (l.DiscountMinor != 0)
                text.AppendLine($"   {L("receipt.discount")} {Euro(-l.DiscountMinor)}");
            if (l.Note is not null)
                text.AppendLine($"   {l.Note}");
        }
        text.AppendLine();
        text.AppendLine($"{L("receipt.total")} {Euro(c.TotalGrossMinor)}");
        if (c.RoundingMinor != 0)
            text.AppendLine($"{L("receipt.rounding")} {Euro(c.RoundingMinor)}");
        foreach (var v in c.Vat)
            text.AppendLine($"{v.Label}: {L("receipt.net")} {Euro(v.NetMinor)} · {L("receipt.vat")} {Euro(v.VatMinor)} · {L("receipt.gross")} {Euro(v.GrossMinor)}");
        foreach (var p in c.Payments)
            text.AppendLine($"{Method(p.Method, language)} {Euro(p.TenderedMinor ?? p.AmountMinor)}");
        if (c.ChangeMinor > 0)
            text.AppendLine($"{L("receipt.change")} {Euro(c.ChangeMinor)}");
        foreach (var n in c.Notes)
        {
            text.AppendLine();
            text.AppendLine(n);
        }
        text.AppendLine();
        text.AppendLine(L("receipt.thanks"));

        string E(string s) => Html.Encode(s);
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"").Append(language).Append('"').Append(rtl ? " dir=\"rtl\"" : "").Append("><head><meta charset=\"utf-8\"><title>").Append(E(subject)).Append("</title></head>");
        html.Append("<body style=\"margin:0;padding:24px;background:#f3f6f7;font-family:Arial,Helvetica,sans-serif;color:#17282e\">");
        html.Append("<table role=\"presentation\" style=\"max-width:480px;margin:0 auto;background:#ffffff;border-radius:8px;padding:20px;width:100%\">");
        html.Append("<tr><td><p style=\"margin:0;font-size:16px;font-weight:bold\">").Append(E(c.SellerName)).Append("</p>");
        html.Append("<p style=\"margin:4px 0 0;font-size:12px;color:#55686e\">");
        if (c.SellerLegalName != c.SellerName)
            html.Append(E(c.SellerLegalName)).Append("<br>");
        html.Append(string.Join("<br>", c.SellerAddress.Select(E)));
        if (!string.IsNullOrWhiteSpace(c.SellerVatId))
            html.Append("<br>").Append(E(L("receipt.vatid"))).Append(": ").Append(E(c.SellerVatId));
        html.Append("</p><h1 style=\"font-size:18px;margin:16px 0 4px\">").Append(E($"{c.Title} {no} {c.ReceiptNumber}")).Append("</h1>");
        html.Append("<p style=\"margin:0 0 12px;font-size:12px;color:#55686e\">").Append(E($"{c.RegisterName} · {Time(c.IssuedAt)}")).Append("</p>");
        html.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:collapse;font-size:13px\">");
        foreach (var l in c.Lines)
        {
            html.Append("<tr><td style=\"padding:4px 0;border-top:1px solid #e3e9eb\">").Append(E($"{l.Quantity} × {l.Description}"));
            if (l.Imei is not null)
                html.Append("<br><span style=\"color:#55686e;font-size:11px\">IMEI ").Append(E(l.Imei)).Append("</span>");
            if (l.DiscountMinor != 0)
                html.Append("<br><span style=\"color:#55686e;font-size:11px\">").Append(E(L("receipt.discount"))).Append(' ').Append(E(Euro(-l.DiscountMinor))).Append("</span>");
            if (l.Note is not null)
                html.Append("<br><span style=\"color:#55686e;font-size:11px\">").Append(E(l.Note)).Append("</span>");
            html.Append("</td><td style=\"padding:4px 0;border-top:1px solid #e3e9eb;text-align:").Append(end).Append(";white-space:nowrap\">")
                .Append(E($"{Euro(l.LineGrossMinor)} {l.TaxLabel}")).Append("</td></tr>");
        }
        void Row(string label, string value, bool bold = false) =>
            html.Append("<tr><td style=\"padding:3px 0").Append(bold ? ";font-weight:bold;font-size:15px" : "").Append("\">").Append(E(label))
                .Append("</td><td style=\"padding:3px 0;text-align:").Append(end).Append(bold ? ";font-weight:bold;font-size:15px" : "").Append("\">")
                .Append(E(value)).Append("</td></tr>");
        Row(L("receipt.total"), Euro(c.TotalGrossMinor), bold: true);
        if (c.RoundingMinor != 0)
            Row(L("receipt.rounding"), Euro(c.RoundingMinor));
        foreach (var v in c.Vat)
            Row($"{v.Label} ({L("receipt.net")} {Euro(v.NetMinor)})", $"{L("receipt.vat")} {Euro(v.VatMinor)}");
        foreach (var p in c.Payments)
            Row(Method(p.Method, language), Euro(p.TenderedMinor ?? p.AmountMinor));
        if (c.ChangeMinor > 0)
            Row(L("receipt.change"), Euro(c.ChangeMinor));
        html.Append("</table>");
        foreach (var n in c.Notes)
            html.Append("<p style=\"margin:10px 0 0;font-size:11px;color:#55686e\">").Append(E(n)).Append("</p>");
        html.Append("<p style=\"margin:16px 0 0;font-size:13px\">").Append(E(L("receipt.thanks"))).Append("</p></td></tr></table></body></html>");

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
