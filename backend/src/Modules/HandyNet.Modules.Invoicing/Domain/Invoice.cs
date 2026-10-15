using HandyNet.SharedKernel;

namespace HandyNet.Modules.Invoicing.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum InvoiceKind
{
    /// <summary>Monthly commission invoice to a seller.</summary>
    Invoice,

    /// <summary>Correction of an issued invoice (Rechnungskorrektur/Storno) for refunded payments; negative amounts.</summary>
    CreditNote,
}

/// <summary>Name, address and tax identifiers of the issuer or the recipient, as printed on the document (snapshot).</summary>
public sealed record InvoiceParty(
    string Name,
    string? LegalName,
    string Street,
    string PostalCode,
    string City,
    string CountryCode,
    string? VatId,
    string? TaxNumber)
{
    /// <summary>Configuration values that still contain this marker are placeholders, not the real company data.</summary>
    public const string PlaceholderMarker = "PLATZHALTER";

    public string DisplayName => string.IsNullOrWhiteSpace(LegalName) ? Name : LegalName;

    public bool IsPlaceholder => new[] { Name, LegalName, Street, PostalCode, City, VatId, TaxNumber }
        .Any(v => v?.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// The platform's own data must be complete (name, address, VAT id or tax number — §14 Abs. 4 UStG). In production,
    /// invoices are refused while the configuration still holds placeholders.
    /// </summary>
    public Result ValidateAsIssuer(bool production)
    {
        if (new[] { Name, Street, PostalCode, City, CountryCode }.Any(string.IsNullOrWhiteSpace)
            || (string.IsNullOrWhiteSpace(VatId) && string.IsNullOrWhiteSpace(TaxNumber)))
            return Error.Conflict("invoicing.issuer_incomplete",
                "The platform's invoice issuer data (name, address, VAT id or tax number) is not configured.");
        if (production && IsPlaceholder)
            return Error.Conflict("invoicing.issuer_not_configured",
                "The invoice issuer data still contains placeholders. Configure Invoicing:Issuer before issuing invoices.");
        return Result.Success();
    }
}

/// <summary>Invoice numbers: gapless and sequential per series and calendar year of the issue date.</summary>
public sealed record InvoiceNumber(InvoiceKind Kind, int Year, long Sequence)
{
    public const long MaxSequence = 999_999;

    public string Prefix => Prefixes(Kind);

    /// <summary>"HN-2026-000001" (invoice) or "HN-RK-2026-000001" (Rechnungskorrektur).</summary>
    public string Text => $"{Prefix}{Year:D4}-{Sequence:D6}";

    public static string Prefixes(InvoiceKind kind) => kind == InvoiceKind.Invoice ? "HN-" : "HN-RK-";

    public static Result<InvoiceNumber> Create(InvoiceKind kind, int year, long sequence) =>
        sequence is < 1 or > MaxSequence || year is < 2000 or > 9999
            ? Error.Validation("invoicing.number_range", $"Invoice sequence {sequence} for {year} is out of range.")
            : new InvoiceNumber(kind, year, sequence);
}

/// <summary>Commission charged on one paid order (input from the Payments module).</summary>
public sealed record CommissionItem(
    Guid PaymentId,
    Guid OrderId,
    string OrderNumber,
    DateTimeOffset PaidAt,
    long NetMinor,
    long VatMinor,
    string Currency);

/// <summary>
/// An issued commission invoice or credit note. Written once and never changed (database triggers); corrections are
/// new credit notes that reference the invoice.
/// </summary>
public sealed class Invoice : IAuditable
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice() { }

    public Guid Id { get; private set; }

    /// <summary>The seller (recipient). Row-Level Security key.</summary>
    public Guid OrganizationId { get; private set; }

    public InvoiceKind Kind { get; private set; }
    public string Number { get; private set; } = null!;
    public int NumberYear { get; private set; }
    public long SequenceNumber { get; private set; }

    /// <summary>Invoice: the service month. Credit note: the month of the refunds it corrects.</summary>
    public DateOnly BillingMonth { get; private set; }

    public DateOnly ServicePeriodStart { get; private set; }
    public DateOnly ServicePeriodEnd { get; private set; }
    public DateOnly IssueDate { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public Guid? ReferencedInvoiceId { get; private set; }
    public string? ReferencedInvoiceNumber { get; private set; }
    public DateOnly? ReferencedInvoiceDate { get; private set; }
    public string Currency { get; private set; } = null!;
    public int VatRateBasisPoints { get; private set; }
    public long TotalNetMinor { get; private set; }
    public long TotalVatMinor { get; private set; }
    public long TotalGrossMinor { get; private set; }
    public InvoiceParty Issuer { get; private set; } = null!;
    public InvoiceParty Recipient { get; private set; } = null!;

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    /// <summary>
    /// Monthly commission invoice: one line per paid order, VAT as snapshotted on each payment (it must equal the
    /// configured rate applied to the line's net, rounded half away from zero), totals = sum of lines.
    /// </summary>
    public static Result<Invoice> IssueCommissionInvoice(Guid organizationId, BillingMonth month, InvoiceNumber number, InvoiceParty issuer,
        InvoiceParty recipient, IReadOnlyList<CommissionItem> items, int vatRateBasisPoints, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (number.Kind != InvoiceKind.Invoice)
            throw new ArgumentException("An invoice needs an invoice number.", nameof(number));
        var charged = items.Where(i => i.NetMinor != 0 || i.VatMinor != 0).OrderBy(i => i.PaidAt).ThenBy(i => i.OrderNumber, StringComparer.Ordinal).ToList();
        if (charged.Count == 0)
            return Error.Conflict("invoicing.nothing_to_invoice", "There is no commission to invoice for this month.");
        if (charged.Select(i => i.PaymentId).Distinct().Count() != charged.Count)
            return Error.Validation("invoicing.duplicate_payment", "A payment can be invoiced only once.");

        var (start, end) = (month.Start(zone), month.End(zone));
        foreach (var item in charged)
        {
            if (item.Currency != Money.Eur)
                return Error.Validation("invoicing.currency", $"Only EUR commission can be invoiced (order {item.OrderNumber}).");
            if (item.NetMinor <= 0 || item.VatMinor < 0)
                return Error.Validation("invoicing.amount", $"Commission of order {item.OrderNumber} must be positive.");
            if (item.VatMinor != Vat(item.NetMinor, vatRateBasisPoints))
                return Error.Validation("invoicing.vat_mismatch",
                    $"VAT of order {item.OrderNumber} ({item.VatMinor}) does not match {vatRateBasisPoints / 100m:0.##} % of {item.NetMinor}.");
            if (item.PaidAt < start || item.PaidAt >= end)
                return Error.Validation("invoicing.outside_period", $"Order {item.OrderNumber} was not paid in {month}.");
        }

        var invoice = New(organizationId, InvoiceKind.Invoice, number, month, month.FirstDay, month.LastDay, issuer, recipient, vatRateBasisPoints, now, zone);
        if (!invoice.IsSuccess)
            return invoice;
        var lineNumber = 0;
        foreach (var item in charged)
            invoice.Value._lines.Add(InvoiceLine.Charge(invoice.Value, ++lineNumber, item));
        invoice.Value.Total();
        return invoice;
    }

    /// <summary>
    /// Correction of <paramref name="original"/> for refunded payments: each line mirrors the original line exactly
    /// with the opposite sign, so invoice + credit note of a fully refunded order add up to zero.
    /// </summary>
    public static Result<Invoice> IssueCreditNote(Invoice original, IReadOnlyCollection<Guid> refundedPaymentIds, BillingMonth refundMonth,
        InvoiceNumber number, InvoiceParty issuer, InvoiceParty recipient, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (number.Kind != InvoiceKind.CreditNote)
            throw new ArgumentException("A credit note needs a credit note number.", nameof(number));
        if (original.Kind != InvoiceKind.Invoice)
            return Error.Validation("invoicing.not_an_invoice", "Only invoices can be corrected.");
        var ids = refundedPaymentIds.Distinct().ToList();
        if (ids.Count == 0)
            return Error.Conflict("invoicing.nothing_to_credit", "There are no refunds to credit.");
        var originalLines = new List<InvoiceLine>();
        foreach (var id in ids)
        {
            var line = original._lines.FirstOrDefault(l => l.PaymentId == id);
            if (line is null)
                return Error.Validation("invoicing.payment_not_on_invoice", $"Payment {id} is not on invoice {original.Number}.");
            originalLines.Add(line);
        }

        var note = New(original.OrganizationId, InvoiceKind.CreditNote, number, refundMonth, original.ServicePeriodStart, original.ServicePeriodEnd,
            issuer, recipient, original.VatRateBasisPoints, now, zone);
        if (!note.IsSuccess)
            return note;
        note.Value.ReferencedInvoiceId = original.Id;
        note.Value.ReferencedInvoiceNumber = original.Number;
        note.Value.ReferencedInvoiceDate = original.IssueDate;
        var lineNumber = 0;
        foreach (var line in originalLines.OrderBy(l => l.LineNumber))
            note.Value._lines.Add(InvoiceLine.Reversal(note.Value, ++lineNumber, line));
        note.Value.Total();
        return note;
    }

    /// <summary>VAT on a net amount, rounded half away from zero (same rule as the commission split in Payments).</summary>
    public static long Vat(long netMinor, int vatRateBasisPoints) => new Money(netMinor, Money.Eur).ApplyRate(vatRateBasisPoints).MinorUnits;

    private static Result<Invoice> New(Guid organizationId, InvoiceKind kind, InvoiceNumber number, BillingMonth billingMonth, DateOnly periodStart,
        DateOnly periodEnd, InvoiceParty issuer, InvoiceParty recipient, int vatRate, DateTimeOffset now, TimeZoneInfo zone)
    {
        var issueDate = InvoiceCalendar.LocalDate(now, zone);
        if (number.Year != issueDate.Year)
            return Error.Validation("invoicing.number_year", $"Number {number.Text} does not belong to the issue year {issueDate.Year}.");
        if (vatRate is < 0 or > 10_000)
            return Error.Validation("invoicing.vat_rate", "Invalid VAT rate.");
        return new Invoice
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Kind = kind,
            Number = number.Text,
            NumberYear = number.Year,
            SequenceNumber = number.Sequence,
            BillingMonth = billingMonth.FirstDay,
            ServicePeriodStart = periodStart,
            ServicePeriodEnd = periodEnd,
            IssueDate = issueDate,
            IssuedAt = now,
            Currency = Money.Eur,
            VatRateBasisPoints = vatRate,
            Issuer = issuer,
            Recipient = recipient,
        };
    }

    private void Total()
    {
        TotalNetMinor = _lines.Sum(l => l.NetMinor);
        TotalVatMinor = _lines.Sum(l => l.VatMinor);
        TotalGrossMinor = TotalNetMinor + TotalVatMinor;
    }
}

public sealed class InvoiceLine
{
    private InvoiceLine() { }

    public Guid InvoiceId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid OrganizationId { get; private set; }
    public InvoiceKind Kind { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public string OrderNumber { get; private set; } = null!;
    public DateTimeOffset PaidAt { get; private set; }
    public long NetMinor { get; private set; }
    public long VatMinor { get; private set; }
    public long GrossMinor { get; private set; }

    /// <summary>Credit note lines: the invoice line they reverse.</summary>
    public Guid? CreditedInvoiceId { get; private set; }

    public int? CreditedLineNumber { get; private set; }

    internal static InvoiceLine Charge(Invoice invoice, int lineNumber, CommissionItem item) => new()
    {
        InvoiceId = invoice.Id,
        LineNumber = lineNumber,
        OrganizationId = invoice.OrganizationId,
        Kind = invoice.Kind,
        PaymentId = item.PaymentId,
        OrderId = item.OrderId,
        OrderNumber = item.OrderNumber,
        PaidAt = item.PaidAt,
        NetMinor = item.NetMinor,
        VatMinor = item.VatMinor,
        GrossMinor = item.NetMinor + item.VatMinor,
    };

    internal static InvoiceLine Reversal(Invoice creditNote, int lineNumber, InvoiceLine original) => new()
    {
        InvoiceId = creditNote.Id,
        LineNumber = lineNumber,
        OrganizationId = creditNote.OrganizationId,
        Kind = creditNote.Kind,
        PaymentId = original.PaymentId,
        OrderId = original.OrderId,
        OrderNumber = original.OrderNumber,
        PaidAt = original.PaidAt,
        NetMinor = -original.NetMinor,
        VatMinor = -original.VatMinor,
        GrossMinor = -original.GrossMinor,
        CreditedInvoiceId = original.InvoiceId,
        CreditedLineNumber = original.LineNumber,
    };
}
