using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>How unit prices of a quotation or invoice are entered: gross (VAT included, like the till) or net (VAT added).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PriceMode>))]
public enum PriceMode
{
    Gross,
    Net,
}

/// <summary>Stored as text. "Expired" is not stored: a draft or sent quotation past its validity date reads as expired.</summary>
public enum QuotationStatus
{
    Draft,
    Sent,
    Accepted,
    Rejected,
}

/// <summary>Stored as text.</summary>
public enum SalesInvoiceKind
{
    Invoice,

    /// <summary>Stornorechnung: the exact negative mirror of an issued invoice, numbered in the same series.</summary>
    CreditNote,
}

/// <summary>Stored as text. Issued invoices are immutable (trigger); a draft may be discarded instead of deleted.</summary>
public enum SalesInvoiceStatus
{
    Draft,
    Issued,
    Discarded,
}

/// <summary>Stored as text. Cash and card at the till go through a POS receipt (<see cref="ReceiptKind.InvoicePayment"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvoicePaymentMethod>))]
public enum InvoicePaymentMethod
{
    BankTransfer,
    Card,
    Cash,
    Other,
}

/// <summary>Derived from the payments of an issued invoice.</summary>
public enum PaymentState
{
    Open,
    PartiallyPaid,
    Paid,
}

/// <summary>
/// The customer of a quotation or invoice as printed (snapshot of the POS customer, or entered). Never email or phone.
/// </summary>
public sealed record SalesCustomer(
    Guid? CustomerId,
    string Name,
    string? Company,
    string? Street,
    string? PostalCode,
    string? City,
    string? CountryCode,
    string? VatId)
{
    public bool IsBusiness => !string.IsNullOrWhiteSpace(Company);

    public static Result<SalesCustomer> Create(Guid? customerId, string? name, string? company, string? street, string? postalCode, string? city,
        string? countryCode, string? vatId)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var n = Clean(name);
        if (n is null || n.Length > Customer.MaxNameLength)
            return Error.Validation("pos.sales_customer_name", $"A customer name (max {Customer.MaxNameLength} characters) is required.");
        if (Clean(company)?.Length > Customer.MaxNameLength || Clean(street)?.Length > Customer.MaxStreetLength
            || Clean(postalCode)?.Length > Customer.MaxPostalCodeLength || Clean(city)?.Length > Customer.MaxCityLength)
            return Error.Validation("pos.sales_customer_field_length", "A customer field is too long.");
        var country = Clean(countryCode)?.ToUpperInvariant();
        if (country is not null && (country.Length != 2 || !country.All(char.IsAsciiLetterUpper)))
            return Error.Validation("pos.sales_customer_country", "The country must be a two-letter ISO code (e.g. DE).");
        var vat = Clean(vatId) is { } v ? Text.NormalizeKey(v) : null;
        if (vat is not null && (vat.Length is < 4 or > Customer.MaxVatIdLength || !vat.All(char.IsAsciiLetterOrDigit)))
            return Error.Validation("pos.sales_customer_vat_id", "The VAT id is not valid.");
        return new SalesCustomer(customerId, n, Clean(company), Clean(street), Clean(postalCode), Clean(city), country, vat);
    }

    public static SalesCustomer From(Customer c) => new(c.Id, c.Name, c.Company, c.Street, c.PostalCode, c.City, c.CountryCode, c.VatId);
}

/// <summary>
/// A line as entered: a catalog product (quantity), a serialized device (IMEI unit, quantity 1) or free text (no stock).
/// Prices in cents, gross or net according to the document's <see cref="PriceMode"/>; §25a lines always carry the final price.
/// </summary>
public sealed record SalesLineInput(
    Guid? ProductId,
    Guid? StockUnitId,
    string? Imei,
    string Description,
    string? Note,
    int Quantity,
    long UnitPriceMinor,
    long DiscountMinor,
    int VatRateBasisPoints,
    bool Margin25a);

/// <summary>A stored line (jsonb). AmountMinor = unit price × quantity − discount, in the document's price mode (negative on credit notes).</summary>
public sealed record SalesLine(
    int Position,
    Guid? ProductId,
    Guid? StockUnitId,
    string? Imei,
    string Description,
    string? Note,
    int Quantity,
    long UnitPriceMinor,
    long DiscountMinor,
    long AmountMinor,
    int VatRateBasisPoints,
    bool Margin25a)
{
    public bool IsFreeText => ProductId is null;

    public string TaxLabel => Margin25a ? "§25a" : $"{VatRateBasisPoints / 100m:0.##} %".Replace('.', ',');
}

/// <summary>Net, VAT and gross per rate for regularly taxed lines (§25a lines are never part of it).</summary>
public sealed record SalesVat(int RateBasisPoints, long NetMinor, long VatMinor, long GrossMinor);

public sealed record SalesTotals(IReadOnlyList<SalesVat> Vat, long MarginGrossMinor, long TotalNetMinor, long TotalVatMinor, long TotalGrossMinor);

/// <summary>§25a internals of an issued invoice line: the unit's purchase price and the margin VAT (stored, never shown or returned).</summary>
public sealed record MarginDetail(int Position, Guid StockUnitId, long PurchasePriceMinor, long MarginVatMinor);

/// <summary>Line rules and totals of quotations and invoices (integer cents, half away from zero).</summary>
public static class SalesCalculator
{
    public const int MaxLines = 200;
    public const int MaxQuantity = 100_000;
    public const int MaxDescriptionLength = 300;
    public const int MaxNoteLength = 500;
    public const long MaxAmountMinor = 100_000_000;
    public static readonly IReadOnlyList<int> Rates = [1900, 700, 0];

    public static Result<IReadOnlyList<SalesLine>> Lines(IReadOnlyList<SalesLineInput> inputs)
    {
        if (inputs.Count > MaxLines)
            return Error.Validation("pos.sales_too_many_lines", $"At most {MaxLines} lines.");
        var units = inputs.Where(l => l.StockUnitId is not null).Select(l => l.StockUnitId).ToList();
        if (units.Distinct().Count() != units.Count)
            return Error.Validation("pos.duplicate_unit", "The same device appears twice.");
        var lines = new List<SalesLine>();
        var position = 0;
        foreach (var l in inputs)
        {
            position++;
            if (l.Quantity is <= 0 or > MaxQuantity)
                return Error.Validation("pos.sales_quantity", $"Line {position}: quantity must be between 1 and {MaxQuantity}.");
            if (l.UnitPriceMinor is < 0 or > MaxAmountMinor)
                return Error.Validation("pos.sales_unit_price", $"Line {position}: the price must be between 0 and 1,000,000.00 €.");
            var gross = checked(l.UnitPriceMinor * l.Quantity);
            if (l.DiscountMinor < 0 || l.DiscountMinor > gross)
                return Error.Validation("pos.sales_discount", $"Line {position}: the discount must be between 0 and the line amount.");
            if (string.IsNullOrWhiteSpace(l.Description) || l.Description.Trim().Length > MaxDescriptionLength)
                return Error.Validation("pos.sales_description", $"Line {position}: a description (max {MaxDescriptionLength} characters) is required.");
            if (l.Note?.Trim().Length > MaxNoteLength)
                return Error.Validation("pos.sales_line_note", $"Line {position}: the note is limited to {MaxNoteLength} characters.");
            if (!Rates.Contains(l.VatRateBasisPoints))
                return Error.Validation("pos.sales_vat_rate", $"Line {position}: the VAT rate must be 19 %, 7 % or 0 %.");
            if (l.StockUnitId is not null && (l.Quantity != 1 || l.ProductId is null))
                return Error.Validation("pos.sales_unit_line", $"Line {position}: a device (IMEI) is sold with quantity 1 and its product.");
            if (l.Margin25a && l.StockUnitId is null)
                return Error.Validation("pos.margin_line", $"Line {position}: §25a applies to a single serialized device (IMEI).");
            lines.Add(new SalesLine(position, l.ProductId, l.StockUnitId, l.Imei?.Trim(), l.Description.Trim(),
                string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim(), l.Quantity, l.UnitPriceMinor, l.DiscountMinor, gross - l.DiscountMinor,
                l.Margin25a ? VatCalculator.StandardRateBasisPoints : l.VatRateBasisPoints, l.Margin25a));
        }
        return lines;
    }

    /// <summary>
    /// Gross mode: VAT per rate is contained in the summed gross of that rate. Net mode: VAT per rate is added to the summed
    /// net. §25a lines always count with their final price (no VAT shown). Totals: gross = Σ gross + margin; net = gross − VAT.
    /// </summary>
    public static SalesTotals Compute(PriceMode mode, IReadOnlyList<SalesLine> lines)
    {
        var vat = lines.Where(l => !l.Margin25a).GroupBy(l => l.VatRateBasisPoints).OrderByDescending(g => g.Key).Select(g =>
        {
            var amount = checked(g.Sum(l => l.AmountMinor));
            if (mode == PriceMode.Gross)
            {
                var v = VatCalculator.VatFromGross(amount, g.Key);
                return new SalesVat(g.Key, amount - v, v, amount);
            }
            var added = VatCalculator.VatOnNet(amount, g.Key);
            return new SalesVat(g.Key, amount, added, checked(amount + added));
        }).ToList();
        var margin = checked(lines.Where(l => l.Margin25a).Sum(l => l.AmountMinor));
        var gross = checked(vat.Sum(v => v.GrossMinor) + margin);
        var totalVat = vat.Sum(v => v.VatMinor);
        return new SalesTotals(vat, margin, gross - totalVat, totalVat, gross);
    }

    public static PaymentState StateOf(long grossMinor, long paidMinor) =>
        paidMinor <= 0 ? PaymentState.Open : paidMinor >= grossMinor ? PaymentState.Paid : PaymentState.PartiallyPaid;
}

/// <summary>
/// A quotation, an invoice or a credit note as printed (PDF, email). For invoices it is stored at issue and never changes
/// (GoBD); quotations are rendered from their current state. A layout, no legal completeness claimed.
/// </summary>
public sealed record SalesDocument(
    string Kind,
    string Title,
    string Number,
    DateOnly Date,
    DateOnly? ServiceDate,
    DateOnly? DueDate,
    DateOnly? ValidUntil,
    SellerInfo Seller,
    SalesCustomer Customer,
    PriceMode PriceMode,
    IReadOnlyList<SalesLine> Lines,
    IReadOnlyList<SalesVat> Vat,
    long MarginGrossMinor,
    long TotalNetMinor,
    long TotalVatMinor,
    long TotalGrossMinor,
    string? ReferencedNumber,
    IReadOnlyList<string> Notes,
    string? FreeText);

public static class SalesNotes
{
    public const string MarginNote = PosInvoice.MarginNote;
    public const string ReverseChargeNote = "Steuerschuldnerschaft des Leistungsempfängers (§ 13b UStG).";

    public static string Due(DateOnly due) => $"Zahlbar ohne Abzug bis {PosInvoice.Date(due)}.";

    public static string ValidUntil(DateOnly date) => $"Dieses Angebot ist gültig bis {PosInvoice.Date(date)}.";

    public static string CreditNoteFor(string number, DateOnly date) => $"Stornorechnung zur Rechnung {number} vom {PosInvoice.Date(date)}.";
}

public sealed record QuotationDetails(SalesCustomer Customer, DateOnly ValidUntil, PriceMode PriceMode, IReadOnlyList<SalesLineInput> Lines,
    string? Notes);

/// <summary>
/// A quotation (Angebot / Preisvorschlag) for a customer. Numbered AN-YYYY-nnnn per organization and year (sequential;
/// allocated in the creating transaction). Editable while Draft or Sent; becomes Accepted when converted into an invoice.
/// No stock is moved or reserved. Never deleted (no DELETE grant).
/// </summary>
public sealed class Quotation : IAuditable
{
    public const string NumberPrefix = "AN";
    public const int MaxNotesLength = 2000;
    public const int MaxValidityDays = 366;

    private Quotation() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Number { get; private set; } = null!;
    public int Year { get; private set; }
    public long SequenceNumber { get; private set; }
    public QuotationStatus Status { get; private set; }
    public Guid? CustomerId { get; private set; }

    [PersonalData]
    public SalesCustomer Customer { get; private set; } = null!;

    /// <summary>Customer name and company for lists and search: generated columns from the customer jsonb (never written by code).</summary>
    [PersonalData]
    public string? CustomerName { get; private set; }

    [PersonalData]
    public string? CustomerCompany { get; private set; }
    public DateOnly IssueDate { get; private set; }
    public DateOnly ValidUntil { get; private set; }
    public PriceMode PriceMode { get; private set; }
    public List<SalesLine> Lines { get; private set; } = [];
    public List<SalesVat> Vat { get; private set; } = [];
    public long MarginGrossMinor { get; private set; }
    public long TotalNetMinor { get; private set; }
    public long TotalVatMinor { get; private set; }
    public long TotalGrossMinor { get; private set; }

    [PersonalData]
    public string? Notes { get; private set; }
    public Guid? ConvertedInvoiceId { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static string FormatNumber(int year, long sequence) => $"{NumberPrefix}-{year}-{sequence:D4}";

    public static Result<Quotation> Create(Guid organizationId, int year, long sequence, DateOnly today, QuotationDetails details, Guid userId,
        DateTimeOffset now)
    {
        if (sequence < 1)
            return Error.Validation("pos.quotation_number", "Quotation numbers start at 1.");
        var q = new Quotation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Year = year,
            SequenceNumber = sequence,
            Number = FormatNumber(year, sequence),
            Status = QuotationStatus.Draft,
            IssueDate = today,
            CreatedAt = now,
            CreatedBy = userId,
        };
        var applied = q.Apply(details, now);
        return applied.IsSuccess ? q : applied.Error!;
    }

    public bool IsEditable => Status is QuotationStatus.Draft or QuotationStatus.Sent && ConvertedInvoiceId is null;

    public bool IsExpired(DateOnly today) => Status is QuotationStatus.Draft or QuotationStatus.Sent && ValidUntil < today;

    /// <summary>Status as shown: Expired for an open quotation past its validity date.</summary>
    public string DisplayStatus(DateOnly today) => IsExpired(today) ? "Expired" : Status.ToString();

    public Result Update(QuotationDetails details, DateTimeOffset now)
    {
        if (!IsEditable)
            return Error.Conflict("pos.quotation_not_editable", "Accepted, rejected or converted quotations cannot be changed. Duplicate it instead.");
        return Apply(details, now);
    }

    public Result MarkSent(DateTimeOffset now)
    {
        if (Status == QuotationStatus.Draft)
            Status = QuotationStatus.Sent;
        SentAt ??= now;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result SetStatus(QuotationStatus status, DateTimeOffset now)
    {
        if (ConvertedInvoiceId is not null)
            return Error.Conflict("pos.quotation_converted", "The quotation was converted into an invoice; its status is final.");
        if (status == Status)
            return Result.Success();
        var allowed = (Status, status) switch
        {
            (QuotationStatus.Draft, QuotationStatus.Sent) => true,
            (QuotationStatus.Draft or QuotationStatus.Sent, QuotationStatus.Accepted or QuotationStatus.Rejected) => true,
            (QuotationStatus.Accepted or QuotationStatus.Rejected, QuotationStatus.Sent) => true, // reopened
            _ => false,
        };
        if (!allowed)
            return Error.Conflict("pos.quotation_status", $"A quotation cannot change from {Status} to {status}.");
        Status = status;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result MarkConverted(Guid invoiceId, DateTimeOffset now)
    {
        if (ConvertedInvoiceId is not null)
            return Error.Conflict("pos.quotation_converted", "The quotation was already converted.");
        if (Status == QuotationStatus.Rejected)
            return Error.Conflict("pos.quotation_rejected", "A rejected quotation cannot be converted. Reopen it first.");
        if (Lines.Count == 0)
            return Error.Validation("pos.sales_empty", "The quotation has no lines.");
        ConvertedInvoiceId = invoiceId;
        Status = QuotationStatus.Accepted;
        UpdatedAt = now;
        return Result.Success();
    }

    public SalesDocument ToDocument(SellerInfo seller)
    {
        var notes = new List<string> { SalesNotes.ValidUntil(ValidUntil) };
        if (MarginGrossMinor != 0)
            notes.Add(SalesNotes.MarginNote);
        return new SalesDocument("Quotation", "Angebot", Number, IssueDate, null, null, ValidUntil, seller, Customer, PriceMode, Lines, Vat,
            MarginGrossMinor, TotalNetMinor, TotalVatMinor, TotalGrossMinor, null, notes, Notes);
    }

    private Result Apply(QuotationDetails d, DateTimeOffset now)
    {
        if (d.ValidUntil < IssueDate || d.ValidUntil.DayNumber - IssueDate.DayNumber > MaxValidityDays)
            return Error.Validation("pos.quotation_valid_until", $"The validity date must be between the quotation date and {MaxValidityDays} days after it.");
        if (d.Notes?.Trim().Length > MaxNotesLength)
            return Error.Validation("pos.sales_notes", $"Notes are limited to {MaxNotesLength} characters.");
        var lines = SalesCalculator.Lines(d.Lines);
        if (!lines.IsSuccess)
            return lines.Error!;
        var totals = SalesCalculator.Compute(d.PriceMode, lines.Value);
        Customer = d.Customer;
        CustomerId = d.Customer.CustomerId;
        ValidUntil = d.ValidUntil;
        PriceMode = d.PriceMode;
        Lines = lines.Value.ToList();
        Vat = totals.Vat.ToList();
        MarginGrossMinor = totals.MarginGrossMinor;
        TotalNetMinor = totals.TotalNetMinor;
        TotalVatMinor = totals.TotalVatMinor;
        TotalGrossMinor = totals.TotalGrossMinor;
        Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim();
        UpdatedAt = now;
        return Result.Success();
    }
}

/// <summary>Counter of the quotation series of one organization and year (+1 allocation only).</summary>
public sealed class QuotationSequence
{
    private QuotationSequence() { }

    public Guid OrganizationId { get; private set; }
    public int Year { get; private set; }
    public long NextNumber { get; private set; }
}

/// <summary>
/// Draft data of an invoice. Customer: may be incomplete in a draft; issuing needs name and full address. ServiceDate: date
/// of supply (null = invoice date). WarehouseId: stock is deducted from it at issue (needed when a line has a product).
/// </summary>
public sealed record SalesInvoiceDetails(
    SalesCustomer? Customer,
    DateOnly? ServiceDate,
    int PaymentTermDays,
    PriceMode PriceMode,
    bool ReverseCharge,
    Guid? WarehouseId,
    IReadOnlyList<SalesLineInput> Lines,
    string? Notes);

/// <summary>
/// A sales invoice outside the till (Rechnung), or its credit note (Stornorechnung). Drafts are editable; issuing takes the
/// next number of the organization's single gapless invoice series (shared with invoices for POS sales, DB-enforced),
/// snapshots seller and customer into an immutable document and deducts the stock. Issued rows never change (trigger, also
/// for the owner role); corrections only by a credit note, which mirrors the invoice negatively and returns the stock.
/// </summary>
public sealed class SalesInvoice : IAuditable
{
    public const int MaxNotesLength = 2000;
    public const int MaxPaymentTermDays = 365;
    public const int MaxReasonLength = 500;
    public const int DefaultPaymentTermDays = 14;

    private SalesInvoice() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public SalesInvoiceKind Kind { get; private set; }
    public SalesInvoiceStatus Status { get; private set; }
    public string? Number { get; private set; }
    public int? Year { get; private set; }
    public long? SequenceNumber { get; private set; }
    public Guid? CustomerId { get; private set; }

    [PersonalData]
    public SalesCustomer? Customer { get; private set; }

    /// <summary>Customer name and company for lists and search: generated columns from the customer jsonb (never written by code).</summary>
    [PersonalData]
    public string? CustomerName { get; private set; }

    [PersonalData]
    public string? CustomerCompany { get; private set; }
    public DateOnly? InvoiceDate { get; private set; }
    public DateOnly? ServiceDate { get; private set; }
    public int PaymentTermDays { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public PriceMode PriceMode { get; private set; }
    public bool ReverseCharge { get; private set; }
    public Guid? WarehouseId { get; private set; }
    public Guid? QuotationId { get; private set; }
    public Guid? OriginalInvoiceId { get; private set; }

    [PersonalData]
    public string? Notes { get; private set; }
    public string? Reason { get; private set; }
    public List<SalesLine> Lines { get; private set; } = [];
    public List<SalesVat> Vat { get; private set; } = [];

    /// <summary>§25a purchase prices and margin VAT of issued lines: internal (tax return), never returned by the API.</summary>
    public List<MarginDetail>? MarginDetails { get; private set; }
    public long MarginGrossMinor { get; private set; }
    public long TotalNetMinor { get; private set; }
    public long TotalVatMinor { get; private set; }
    public long TotalGrossMinor { get; private set; }

    [PersonalData]
    public SalesDocument? Document { get; private set; }
    public DateTimeOffset? IssuedAt { get; private set; }
    public Guid? IssuedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<SalesInvoice> CreateDraft(Guid organizationId, SalesInvoiceDetails details, Guid? quotationId, Guid userId, DateTimeOffset now)
    {
        var invoice = new SalesInvoice
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Kind = SalesInvoiceKind.Invoice,
            Status = SalesInvoiceStatus.Draft,
            QuotationId = quotationId,
            CreatedAt = now,
            CreatedBy = userId,
        };
        var applied = invoice.Apply(details, now);
        return applied.IsSuccess ? invoice : applied.Error!;
    }

    public Result UpdateDraft(SalesInvoiceDetails details, DateTimeOffset now) =>
        Status != SalesInvoiceStatus.Draft
            ? Error.Conflict("pos.sales_invoice_issued", "An issued invoice cannot be changed. Cancel it with a credit note.")
            : Apply(details, now);

    public Result Discard(DateTimeOffset now)
    {
        if (Status != SalesInvoiceStatus.Draft)
            return Error.Conflict("pos.sales_invoice_issued", "Only drafts can be discarded.");
        Status = SalesInvoiceStatus.Discarded;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Everything the issue needs, checked before a number is taken (a refusal never touches the counter): lines, the
    /// seller's VAT id, the customer's name and full address (§14 Abs. 4 UStG), a VAT id for reverse charge, a warehouse when
    /// stock is moved.
    /// </summary>
    public Result<InvoiceCustomer> CheckIssuable(SellerInfo seller)
    {
        if (Kind != SalesInvoiceKind.Invoice || Status != SalesInvoiceStatus.Draft)
            return Error.Conflict("pos.sales_invoice_issued", "Only draft invoices can be issued.");
        if (Lines.Count == 0)
            return Error.Validation("pos.sales_empty", "Add at least one line.");
        if (TotalGrossMinor <= 0)
            return Error.Validation("pos.sales_total_not_positive", "The invoice total must be positive.");
        if (string.IsNullOrWhiteSpace(seller.VatId))
            return Error.Conflict("pos.invoice_seller_vat_id_missing",
                "The organization has no VAT id; an invoice (§14 UStG) needs the seller's VAT id or tax number. Add it to the organization profile.");
        if (Customer is null)
            return Error.Validation("pos.invoice_customer_required", "Name and address of the customer are required for an invoice.");
        var customer = InvoiceCustomer.Create(Customer.Name, Customer.Company, Customer.Street, Customer.PostalCode, Customer.City, Customer.CountryCode,
            Customer.VatId);
        if (!customer.IsSuccess)
            return customer.Error!;
        if (ReverseCharge && string.IsNullOrWhiteSpace(Customer.VatId))
            return Error.Validation("pos.reverse_charge_vat_id", "Reverse charge needs the customer's VAT id.");
        if (Lines.Any(l => !l.IsFreeText) && WarehouseId is null)
            return Error.Validation("pos.sales_warehouse_required", "Choose the warehouse the goods are taken from.");
        return customer.Value;
    }

    /// <summary>Issues the draft with the allocated number; <paramref name="margins"/> are the §25a internals of the staged units.</summary>
    public Result Issue(SellerInfo seller, int year, long sequence, DateOnly invoiceDate, IReadOnlyList<MarginDetail> margins, Guid userId,
        DateTimeOffset now)
    {
        var check = CheckIssuable(seller);
        if (!check.IsSuccess)
            return check.Error!;
        if (sequence < 1)
            return Error.Validation("pos.invoice_number", "Invoice numbers start at 1.");
        Status = SalesInvoiceStatus.Issued;
        Year = year;
        SequenceNumber = sequence;
        Number = PosInvoice.FormatNumber(year, sequence);
        InvoiceDate = invoiceDate;
        ServiceDate ??= invoiceDate;
        DueDate = invoiceDate.AddDays(PaymentTermDays);
        MarginDetails = margins.Count == 0 ? null : margins.ToList();
        IssuedAt = now;
        IssuedBy = userId;
        UpdatedAt = now;
        var notes = new List<string>();
        if (MarginGrossMinor != 0)
            notes.Add(SalesNotes.MarginNote);
        if (ReverseCharge)
            notes.Add(SalesNotes.ReverseChargeNote);
        notes.Add(SalesNotes.Due(DueDate.Value));
        Document = new SalesDocument("Invoice", "Rechnung", Number, invoiceDate, ServiceDate, DueDate, null, seller, Customer!, PriceMode, Lines, Vat,
            MarginGrossMinor, TotalNetMinor, TotalVatMinor, TotalGrossMinor, null, notes, Notes);
        return Result.Success();
    }

    /// <summary>
    /// The credit note (Stornorechnung) of an issued invoice: every line, VAT amount and total mirrored negatively, numbered in
    /// the same series, referencing the original; its stock goes back (by the caller). At most one per invoice (unique index).
    /// </summary>
    public static Result<SalesInvoice> CreditNoteFor(SalesInvoice original, SellerInfo seller, int year, long sequence, DateOnly date, string reason,
        Guid userId, DateTimeOffset now)
    {
        if (original.Kind != SalesInvoiceKind.Invoice || original.Status != SalesInvoiceStatus.Issued)
            return Error.Conflict("pos.sales_invoice_not_issued", "Only issued invoices can be cancelled with a credit note.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"A reason (max {MaxReasonLength} characters) is required.");
        if (sequence < 1)
            return Error.Validation("pos.invoice_number", "Invoice numbers start at 1.");
        var number = PosInvoice.FormatNumber(year, sequence);
        var lines = original.Lines.Select(l => l with { Quantity = -l.Quantity, DiscountMinor = -l.DiscountMinor, AmountMinor = -l.AmountMinor }).ToList();
        var vat = original.Vat.Select(v => new SalesVat(v.RateBasisPoints, -v.NetMinor, -v.VatMinor, -v.GrossMinor)).ToList();
        var notes = new List<string> { SalesNotes.CreditNoteFor(original.Number!, original.InvoiceDate!.Value), $"Grund: {reason.Trim()}" };
        if (original.MarginGrossMinor != 0)
            notes.Add(SalesNotes.MarginNote);
        if (original.ReverseCharge)
            notes.Add(SalesNotes.ReverseChargeNote);
        var note = new SalesInvoice
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = original.OrganizationId,
            Kind = SalesInvoiceKind.CreditNote,
            Status = SalesInvoiceStatus.Issued,
            Number = number,
            Year = year,
            SequenceNumber = sequence,
            CustomerId = original.CustomerId,
            Customer = original.Customer,
            InvoiceDate = date,
            ServiceDate = original.ServiceDate,
            PaymentTermDays = 0,
            DueDate = date,
            PriceMode = original.PriceMode,
            ReverseCharge = original.ReverseCharge,
            WarehouseId = original.WarehouseId,
            OriginalInvoiceId = original.Id,
            Reason = reason.Trim(),
            Lines = lines,
            Vat = vat,
            MarginDetails = original.MarginDetails?.Select(m => m with { PurchasePriceMinor = -m.PurchasePriceMinor, MarginVatMinor = -m.MarginVatMinor })
                .ToList(),
            MarginGrossMinor = -original.MarginGrossMinor,
            TotalNetMinor = -original.TotalNetMinor,
            TotalVatMinor = -original.TotalVatMinor,
            TotalGrossMinor = -original.TotalGrossMinor,
            IssuedAt = now,
            IssuedBy = userId,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
        };
        note.Document = new SalesDocument("CreditNote", "Stornorechnung", number, date, original.ServiceDate, null, null, seller, original.Customer!,
            original.PriceMode, lines, vat, note.MarginGrossMinor, note.TotalNetMinor, note.TotalVatMinor, note.TotalGrossMinor, original.Number, notes, null);
        return note;
    }

    /// <summary>A new draft with the same content (customer, lines, terms); never the number or issue data.</summary>
    public SalesInvoiceDetails ToDetails() => new(Customer, null, PaymentTermDays, PriceMode, ReverseCharge, WarehouseId,
        Lines.Select(l => new SalesLineInput(l.ProductId, l.StockUnitId, l.Imei, l.Description, l.Note, Math.Abs(l.Quantity), l.UnitPriceMinor,
            Math.Abs(l.DiscountMinor), l.VatRateBasisPoints, l.Margin25a)).ToList(), Notes);

    private Result Apply(SalesInvoiceDetails d, DateTimeOffset now)
    {
        if (d.PaymentTermDays is < 0 or > MaxPaymentTermDays)
            return Error.Validation("pos.payment_term_days", $"The payment term must be between 0 and {MaxPaymentTermDays} days.");
        if (d.Notes?.Trim().Length > MaxNotesLength)
            return Error.Validation("pos.sales_notes", $"Notes are limited to {MaxNotesLength} characters.");
        var lines = SalesCalculator.Lines(d.Lines);
        if (!lines.IsSuccess)
            return lines.Error!;
        if (d.ReverseCharge && lines.Value.Any(l => l.Margin25a || l.VatRateBasisPoints != 0))
            return Error.Validation("pos.reverse_charge_lines", "With reverse charge every line must be at 0 % VAT and §25a lines are not possible.");
        var totals = SalesCalculator.Compute(d.PriceMode, lines.Value);
        Customer = d.Customer;
        CustomerId = d.Customer?.CustomerId;
        ServiceDate = d.ServiceDate;
        PaymentTermDays = d.PaymentTermDays;
        PriceMode = d.PriceMode;
        ReverseCharge = d.ReverseCharge;
        WarehouseId = d.WarehouseId;
        Lines = lines.Value.ToList();
        Vat = totals.Vat.ToList();
        MarginGrossMinor = totals.MarginGrossMinor;
        TotalNetMinor = totals.TotalNetMinor;
        TotalVatMinor = totals.TotalVatMinor;
        TotalGrossMinor = totals.TotalGrossMinor;
        Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim();
        UpdatedAt = now;
        return Result.Success();
    }
}

/// <summary>
/// A payment on an issued invoice. Append-only (GoBD): never changed or deleted. Bank transfers and other payments are just
/// recorded; cash and card in the shop are collected at the till, which writes the POS receipt (<see cref="ReceiptId"/>) in
/// the same transaction. The database refuses payments beyond the open amount and on cancelled invoices.
/// </summary>
public sealed class SalesInvoicePayment : IAuditable
{
    public const int MaxReferenceLength = 200;

    private SalesInvoicePayment() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public InvoicePaymentMethod Method { get; private set; }
    public long AmountMinor { get; private set; }
    public DateOnly PaidOn { get; private set; }
    public Guid? ReceiptId { get; private set; }
    public string? Reference { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    /// <param name="paidMinor">Already paid on the invoice.</param>
    /// <param name="cancelled">A credit note exists for the invoice.</param>
    public static Result<SalesInvoicePayment> Record(SalesInvoice invoice, InvoicePaymentMethod method, long amountMinor, DateOnly paidOn, string? reference,
        long paidMinor, bool cancelled, Guid? receiptId, Guid userId, DateTimeOffset now)
    {
        if (invoice.Kind != SalesInvoiceKind.Invoice || invoice.Status != SalesInvoiceStatus.Issued)
            return Error.Conflict("pos.sales_invoice_not_issued", "Payments are recorded on issued invoices only.");
        if (cancelled)
            return Error.Conflict("pos.sales_invoice_cancelled", "The invoice was cancelled with a credit note.");
        if (amountMinor <= 0)
            return Error.Validation("pos.payment_amount", "The amount must be positive.");
        var open = invoice.TotalGrossMinor - paidMinor;
        if (amountMinor > open)
            return Error.Conflict("pos.payment_exceeds_open", $"Only {open / 100m:0.00} € are open on this invoice.")
                .WithDetails(new Dictionary<string, object?> { ["openMinor"] = open });
        if ((method is InvoicePaymentMethod.Cash) && receiptId is null)
            return Error.Validation("pos.cash_payment_at_till", "Cash received in the shop is collected at the till (a receipt is issued).");
        if (reference?.Trim().Length > MaxReferenceLength)
            return Error.Validation("pos.payment_reference", $"The reference is limited to {MaxReferenceLength} characters.");
        if (invoice.InvoiceDate is { } issued && paidOn < issued.AddDays(-366))
            return Error.Validation("pos.payment_date", "The payment date is implausible.");
        return new SalesInvoicePayment
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = invoice.OrganizationId,
            InvoiceId = invoice.Id,
            Method = method,
            AmountMinor = amountMinor,
            PaidOn = paidOn,
            ReceiptId = receiptId,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            CreatedAt = now,
            CreatedBy = userId,
        };
    }
}
