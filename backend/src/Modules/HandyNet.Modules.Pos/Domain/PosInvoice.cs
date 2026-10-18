using System.Globalization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>Recipient of an invoice for a POS sale (§14 Abs. 4 Nr. 1 UStG: full name and address).</summary>
public sealed record InvoiceCustomer(string Name, string? Company, string Street, string PostalCode, string City, string CountryCode, string? VatId)
{
    public const int MaxNameLength = 200;

    public static Result<InvoiceCustomer> Create(string? name, string? company, string? street, string? postalCode, string? city, string? countryCode,
        string? vatId)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var n = Clean(name);
        var s = Clean(street);
        var p = Clean(postalCode);
        var c = Clean(city);
        var cc = Clean(countryCode)?.ToUpperInvariant() ?? "DE";
        if (n is null || s is null || p is null || c is null)
            return Error.Validation("pos.invoice_customer_address", "An invoice needs the customer's name and full address (street, postal code, city).");
        if (n.Length > MaxNameLength || (Clean(company)?.Length ?? 0) > MaxNameLength || s.Length > 200 || p.Length > 10 || c.Length > 100)
            return Error.Validation("pos.invoice_customer_address", "A customer field is too long.");
        if (cc.Length != 2 || !cc.All(char.IsAsciiLetterUpper))
            return Error.Validation("pos.invoice_customer_country", "The country must be a two-letter ISO code (e.g. DE).");
        var v = Clean(vatId)?.Replace(" ", "").ToUpperInvariant();
        if (v?.Length > 20)
            return Error.Validation("pos.invoice_customer_vat_id", "The VAT id is too long.");
        return new InvoiceCustomer(n, Clean(company), s, p, c, cc, v);
    }
}

public sealed record InvoiceLine(int Position, string Description, string? Imei, int Quantity, long UnitPriceMinor, long DiscountMinor, long LineGrossMinor,
    string TaxLabel, string? Note);

/// <summary>Net, VAT and gross per VAT rate (regularly taxed lines only).</summary>
public sealed record InvoiceVat(int RateBasisPoints, long NetMinor, long VatMinor, long GrossMinor);

/// <summary>
/// The invoice exactly as issued (stored as jsonb, rendered to PDF on download): German "Rechnung" with the fields of
/// §14 Abs. 4 UStG — seller and customer name and address, the seller's VAT id, issue date, unique number from a gapless
/// series, date of supply, quantity and description, net amounts and VAT per rate, gross total; §25a lines are shown
/// without VAT and with the note required by §14a Abs. 6 UStG. A template, no legal completeness claimed.
/// </summary>
public sealed record PosInvoiceDocument(
    string Title,
    string Number,
    DateOnly IssueDate,
    DateOnly SupplyDate,
    SellerInfo Seller,
    InvoiceCustomer Customer,
    string RegisterName,
    long ReceiptNumber,
    IReadOnlyList<InvoiceLine> Lines,
    IReadOnlyList<InvoiceVat> Vat,
    long MarginGrossMinor,
    long TotalNetMinor,
    long TotalVatMinor,
    long TotalGrossMinor,
    IReadOnlyList<string> PaymentLines,
    IReadOnlyList<string> Notes);

/// <summary>
/// An invoice for a POS sale (Rechnung), issued from a sale receipt for a named customer. One per receipt; immutable
/// (GoBD, triggers); numbers <c>RE-YYYY-nnnnnn</c> per organization and year, gapless and enforced by the database.
/// </summary>
public sealed class PosInvoice : IAuditable
{
    public const string NumberPrefix = "RE";
    public const string MarginNote =
        "Gebrauchtgegenstände/Sonderregelung – Differenzbesteuerung nach § 25a UStG. Die Umsatzsteuer wird für diese Positionen nicht gesondert ausgewiesen.";

    private PosInvoice() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public string Number { get; private set; } = null!;
    public int Year { get; private set; }
    public long SequenceNumber { get; private set; }
    public DateOnly IssueDate { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public Guid IssuedBy { get; private set; }

    /// <summary>Recipient name/address (personal data of a private customer); kept with the invoice as issued.</summary>
    [PersonalData]
    public InvoiceCustomer Customer { get; private set; } = null!;

    [PersonalData]
    public PosInvoiceDocument Document { get; private set; } = null!;
    public long TotalNetMinor { get; private set; }
    public long TotalVatMinor { get; private set; }
    public long TotalGrossMinor { get; private set; }

    public static string FormatNumber(int year, long sequence) => $"{NumberPrefix}-{year}-{sequence:D6}";

    /// <summary>
    /// Builds the invoice for a sale receipt. Refused for refunds and for receipts that were cancelled or (partly)
    /// returned — those need an invoice correction, which is not supported yet — and when the seller has no VAT id.
    /// </summary>
    public static Result<PosInvoice> Issue(Receipt receipt, bool cancelledOrReturned, SellerInfo seller, InvoiceCustomer customer, string registerName,
        int year, long sequence, DateOnly issueDate, DateOnly supplyDate, Guid userId, DateTimeOffset now)
    {
        if (receipt.Kind != ReceiptKind.Sale)
            return Error.Conflict("pos.invoice_not_a_sale", "Invoices are issued for sale receipts only.");
        if (cancelledOrReturned)
            return Error.Conflict("pos.invoice_receipt_refunded", "The receipt was cancelled or items were returned; an invoice cannot be issued for it.");
        if (string.IsNullOrWhiteSpace(seller.VatId))
            return Error.Conflict("pos.invoice_seller_vat_id_missing",
                "The organization has no VAT id; an invoice (§14 UStG) needs the seller's VAT id or tax number. Add it to the organization profile.");
        if (sequence < 1)
            return Error.Validation("pos.invoice_number", "Invoice numbers start at 1.");

        var number = FormatNumber(year, sequence);
        var lines = receipt.Lines.OrderBy(l => l.LineNumber).Select(l => new InvoiceLine(l.LineNumber, l.Description, l.Imei, l.Quantity, l.UnitPriceMinor,
            l.DiscountMinor, l.LineGrossMinor, l.TaxTreatment == TaxTreatment.Margin25a ? ReceiptDocument.MarginLabel : ReceiptDocument.StandardLabel,
            l.Note)).ToList();
        var standard = receipt.Lines.Where(l => l.TaxTreatment == TaxTreatment.Standard19).ToList();
        var vat = standard.GroupBy(l => l.VatRateBasisPoints).OrderBy(g => g.Key)
            .Select(g => new InvoiceVat(g.Key, g.Sum(l => l.NetMinor), g.Sum(l => l.VatMinor), g.Sum(l => l.LineGrossMinor))).ToList();
        // §25a: gross only; the margin VAT inside it is never shown (it would reveal the purchase price).
        var margin = receipt.Lines.Where(l => l.TaxTreatment == TaxTreatment.Margin25a).Sum(l => l.LineGrossMinor);
        var shownNet = vat.Sum(v => v.NetMinor);
        var shownVat = vat.Sum(v => v.VatMinor);
        var total = receipt.TotalGrossMinor;
        if (shownNet + shownVat + margin != total)
            return Error.Conflict("pos.invoice_totals", "The receipt totals do not add up; the invoice cannot be issued.");

        var notes = new List<string>();
        if (margin > 0)
            notes.Add(MarginNote);
        notes.Add($"Der Betrag wurde an der Kasse bezahlt (Kassenbeleg Nr. {receipt.Number}, {registerName}).");
        var payments = receipt.Payments.Select(p => $"{(p.Method == PaymentMethod.Cash ? "Bar" : "Karte")}: {Euro(p.AmountMinor)}").ToList();

        var document = new PosInvoiceDocument("Rechnung", number, issueDate, supplyDate, seller, customer, registerName, receipt.Number, lines, vat, margin,
            shownNet, shownVat, total, payments, notes);
        return new PosInvoice
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = receipt.OrganizationId,
            ReceiptId = receipt.Id,
            Number = number,
            Year = year,
            SequenceNumber = sequence,
            IssueDate = issueDate,
            IssuedAt = now,
            IssuedBy = userId,
            Customer = customer,
            Document = document,
            TotalNetMinor = shownNet + margin,
            TotalVatMinor = shownVat,
            TotalGrossMinor = total,
        };
    }

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Euro(long minor) => (minor / 100m).ToString("#,##0.00", German) + " €";

    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", German);
}

/// <summary>Counter of the gapless invoice series of one organization and year; never written except by the +1 allocation.</summary>
public sealed class PosInvoiceSequence
{
    private PosInvoiceSequence() { }

    public Guid OrganizationId { get; private set; }
    public int Year { get; private set; }
    public long NextNumber { get; private set; }
}
