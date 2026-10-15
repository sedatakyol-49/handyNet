using System.Globalization;

namespace HandyNet.Modules.Invoicing.Domain;

public sealed record DocumentField(string Label, string Value);

public sealed record DocumentLine(string OrderNumber, string PaidOn, string Net, string Vat, string Gross);

/// <summary>Everything printed on an invoice or credit note, already formatted (German). Rendering only lays it out.</summary>
public sealed record InvoiceDocumentContent(
    string Title,
    string Number,
    IReadOnlyList<string> IssuerBlock,
    IReadOnlyList<string> RecipientBlock,
    IReadOnlyList<DocumentField> Header,
    string ServiceDescription,
    IReadOnlyList<DocumentLine> Lines,
    IReadOnlyList<DocumentField> Totals,
    IReadOnlyList<string> Notes,
    string Footer)
{
    public IEnumerable<string> AllText()
    {
        yield return Title;
        yield return Number;
        foreach (var s in IssuerBlock.Concat(RecipientBlock))
            yield return s;
        foreach (var f in Header.Concat(Totals))
        {
            yield return f.Label;
            yield return f.Value;
        }
        yield return ServiceDescription;
        foreach (var l in Lines)
        {
            yield return l.OrderNumber;
            yield return l.PaidOn;
            yield return l.Net;
            yield return l.Vat;
            yield return l.Gross;
        }
        foreach (var n in Notes)
            yield return n;
        yield return Footer;
    }
}

/// <summary>
/// German layout of a commission invoice / invoice correction with the invoice fields of §14 Abs. 4 UStG: issuer and
/// recipient name and address, the issuer's VAT id / tax number, issue date, unique number, the service and its period,
/// net amount, VAT rate and amount, gross amount; a correction names the corrected invoice. The wording is a template —
/// no claim of legal completeness.
/// </summary>
public static class InvoiceDocument
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public const string ServiceText = "Vermittlungsprovision für über den HandyNet-B2B-Marktplatz online bezahlte Bestellungen";

    public static InvoiceDocumentContent Build(Invoice invoice, TimeZoneInfo zone)
    {
        var isCredit = invoice.Kind == InvoiceKind.CreditNote;
        var title = isCredit ? "Rechnungskorrektur (Storno)" : "Rechnung";
        var rate = $"{invoice.VatRateBasisPoints / 100m:0.##} %".Replace('.', ',');

        var header = new List<DocumentField>
        {
            new(isCredit ? "Korrekturnummer" : "Rechnungsnummer", invoice.Number),
            new(isCredit ? "Datum" : "Rechnungsdatum", Date(invoice.IssueDate)),
            new("Leistungszeitraum", $"{Date(invoice.ServicePeriodStart)} – {Date(invoice.ServicePeriodEnd)}"),
        };
        if (isCredit)
            header.Add(new("Korrektur zur Rechnung",
                $"{invoice.ReferencedInvoiceNumber} vom {(invoice.ReferencedInvoiceDate is { } d ? Date(d) : "–")}"));
        if (!string.IsNullOrWhiteSpace(invoice.Recipient.VatId))
            header.Add(new("USt-IdNr. Empfänger", invoice.Recipient.VatId));

        var totals = new List<DocumentField>
        {
            new("Summe netto", Euro(invoice.TotalNetMinor)),
            new($"Umsatzsteuer {rate}", Euro(invoice.TotalVatMinor)),
            new(isCredit ? "Korrekturbetrag brutto" : "Gesamtbetrag brutto", Euro(invoice.TotalGrossMinor)),
        };

        var notes = new List<string>();
        if (isCredit)
            notes.Add($"Die folgenden Bestellungen wurden erstattet. Die dafür berechnete Provision und Umsatzsteuer aus der Rechnung "
                      + $"{invoice.ReferencedInvoiceNumber} werden hiermit storniert; der Umsatzsteuerbetrag mindert sich entsprechend.");
        else
            notes.Add("Die Provision wurde bei der Auszahlung des Verkaufserlöses bereits einbehalten. Es ist keine Zahlung erforderlich.");
        notes.Add("Die Umsatzsteuer wird je Bestellung auf die Provision berechnet (kaufmännisch gerundet) und summiert.");
        if (invoice.Issuer.IsPlaceholder)
            notes.Add("MUSTER – Die Ausstellerdaten sind Platzhalter. Dies ist keine gültige Rechnung.");

        return new InvoiceDocumentContent(
            title,
            invoice.Number,
            PartyBlock(invoice.Issuer, withTaxIds: true),
            PartyBlock(invoice.Recipient, withTaxIds: false),
            header,
            ServiceText,
            invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new DocumentLine(l.OrderNumber,
                Date(InvoiceCalendar.LocalDate(l.PaidAt, zone)), Euro(l.NetMinor), Euro(l.VatMinor), Euro(l.GrossMinor))).ToList(),
            totals,
            notes,
            $"{title} {invoice.Number} · {invoice.Issuer.DisplayName}");
    }

    public static string Euro(long minor) => (minor / 100m).ToString("#,##0.00", German) + " €";

    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", German);

    private static List<string> PartyBlock(InvoiceParty party, bool withTaxIds)
    {
        var lines = new List<string> { party.DisplayName };
        if (!string.Equals(party.DisplayName, party.Name, StringComparison.Ordinal))
            lines.Add(party.Name);
        lines.Add(party.Street);
        lines.Add($"{party.PostalCode} {party.City}" + (party.CountryCode is "DE" ? string.Empty : $", {party.CountryCode}"));
        if (withTaxIds)
        {
            if (!string.IsNullOrWhiteSpace(party.VatId))
                lines.Add($"USt-IdNr.: {party.VatId}");
            if (!string.IsNullOrWhiteSpace(party.TaxNumber))
                lines.Add($"Steuernummer: {party.TaxNumber}");
        }
        return lines;
    }
}
