namespace HandyNet.Modules.Pos.Domain;

public sealed record SellerInfo(string Name, string LegalName, string Street, string PostalCode, string City, string CountryCode, string? VatId);

public sealed record DocumentLine(
    string Description,
    string? Imei,
    int Quantity,
    long UnitPriceMinor,
    long DiscountMinor,
    long LineGrossMinor,
    string TaxLabel,
    string? Note = null);

/// <summary>The customer as printed on the receipt (no email or phone). Absent on receipts without a customer.</summary>
public sealed record DocumentCustomer(
    Guid Id,
    string Name,
    string? Company,
    string? Street,
    string? PostalCode,
    string? City,
    string? CountryCode,
    string? VatId);

/// <summary>Offline sale (ADR-0022): when and under which device reference it was recorded before it was booked.</summary>
public sealed record DocumentOffline(DateTimeOffset RecordedAt, string Reference);

/// <summary>VAT breakdown as printed: only regularly taxed lines. §25a VAT is never shown on the receipt.</summary>
public sealed record DocumentVat(string Label, int RateBasisPoints, long GrossMinor, long NetMinor, long VatMinor);

public sealed record DocumentPayment(string Method, long AmountMinor, long? TenderedMinor, DocumentCard? Card = null);

/// <summary>
/// What the card terminal reported for a card payment (ADR-0021), printed under the payment. IsTest: simulated terminal,
/// no card was charged. The card number is always masked.
/// </summary>
public sealed record DocumentCard(
    string? CardName,
    string? MaskedPan,
    string? TraceNumber,
    string? AuthCode,
    string? TerminalId,
    string? TerminalReceiptNumber,
    bool IsTest = false)
{
    public static DocumentCard From(CardTransaction t) =>
        new(t.CardName, t.MaskedPan, t.TraceNumber, t.AuthCode, t.TerminalId, t.TerminalReceiptNumber, t.IsTest);
}

public sealed record DocumentFiscal(
    string Status,
    string? Notice,
    string? TseSerialNumber,
    long? TransactionNumber,
    long? SignatureCounter,
    string? Signature,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Mode = null,
    bool IsTest = false,
    string? ClientId = null,
    string? SignatureAlgorithm = null,
    string? TimeFormat = null,
    string? PublicKey = null,
    string? ProcessType = null,
    string? ProcessData = null,
    string? QrPayload = null);

/// <summary>
/// The printable receipt exactly as issued, stored with the receipt (jsonb) so a reprint always shows the original —
/// seller data, register, number, time, lines, VAT breakdown, payments and fiscal data at that moment.
/// </summary>
public sealed record ReceiptDocument(
    string Title,
    SellerInfo Seller,
    Guid RegisterId,
    string RegisterName,
    long ReceiptNumber,
    string Kind,
    long? ReferencedReceiptNumber,
    DateTimeOffset IssuedAt,
    string Currency,
    IReadOnlyList<DocumentLine> Lines,
    IReadOnlyList<DocumentVat> Vat,
    long TotalGrossMinor,
    IReadOnlyList<DocumentPayment> Payments,
    long ChangeMinor,
    IReadOnlyList<string> Notes,
    DocumentFiscal Fiscal,
    DocumentCustomer? Customer = null,
    long RoundingMinor = 0,
    DocumentOffline? Offline = null,
    string? PricelistName = null)
{
    public const string StandardLabel = "A";
    public const string MarginLabel = "§25a";
    public const string MarginSchemeNote =
        "Gebrauchtgegenstände/Sonderregelung (Differenzbesteuerung nach § 25a UStG): Die enthaltene Umsatzsteuer wird nicht gesondert ausgewiesen.";
    public const string NotConnectedNotice = "TSE nicht angebunden – Testbetrieb";
    public const string NotTaxableLabel = "N";
    public const string InvoicePaymentNote =
        "Zahlung auf eine Rechnung (Forderungsauflösung, nicht steuerbar): Die Umsatzsteuer ist in der Rechnung ausgewiesen.";

    public static string LabelFor(TaxTreatment treatment) => treatment switch
    {
        TaxTreatment.Margin25a => MarginLabel,
        TaxTreatment.NotTaxable => NotTaxableLabel,
        _ => StandardLabel,
    };

    public static ReceiptDocument For(Receipt receipt, SellerInfo seller, string registerName, long? referencedReceiptNumber, FiscalData fiscal)
    {
        var lines = receipt.Lines.Select(l => new DocumentLine(l.Description, l.Imei, l.Quantity, l.UnitPriceMinor, l.DiscountMinor,
            l.LineGrossMinor, LabelFor(l.TaxTreatment), l.Note)).ToList();

        var vat = receipt.Lines.Where(l => l.TaxTreatment == TaxTreatment.Standard19)
            .GroupBy(l => l.VatRateBasisPoints)
            .Select(g => new DocumentVat($"{StandardLabel} {g.Key / 100m:0.##} %", g.Key, g.Sum(l => l.LineGrossMinor), g.Sum(l => l.NetMinor),
                g.Sum(l => l.VatMinor)))
            .ToList();

        var notes = new List<string>();
        if (receipt.Kind == ReceiptKind.InvoicePayment)
            notes.Add(InvoicePaymentNote);
        if (receipt.Lines.Any(l => l.TaxTreatment == TaxTreatment.Margin25a))
            notes.Add(MarginSchemeNote);
        if (fiscal.Status == FiscalStatus.NotConnected)
            notes.Add(fiscal.Notice ?? NotConnectedNotice);
        if (fiscal.Status == FiscalStatus.Failed)
            notes.Add(KassenSichV.OutageNotice);
        if (fiscal.IsTest)
            notes.Add(KassenSichV.TestTseNotice);
        if (receipt.Offline is { } offline)
            notes.Add(OfflineNote(offline));

        var title = receipt.Kind switch
        {
            ReceiptKind.Storno => "Storno-Beleg",
            ReceiptKind.Return => "Rückgabe-Beleg",
            ReceiptKind.InvoicePayment => "Zahlungsbeleg",
            _ => "Kassenbeleg",
        };

        return new ReceiptDocument(title, seller, receipt.RegisterId, registerName, receipt.Number, receipt.Kind.ToString(), referencedReceiptNumber,
            receipt.CreatedAt, receipt.Currency, lines, vat, receipt.TotalGrossMinor,
            receipt.Payments.Select(p => new DocumentPayment(p.Method.ToString(), p.AmountMinor, p.TenderedMinor)).ToList(),
            receipt.ChangeMinor, notes,
            new DocumentFiscal(fiscal.Status.ToString(), fiscal.Notice, fiscal.TseSerialNumber, fiscal.TransactionNumber, fiscal.SignatureCounter,
                fiscal.Signature, fiscal.StartedAt, fiscal.FinishedAt, fiscal.Mode, fiscal.IsTest, fiscal.ClientId, fiscal.SignatureAlgorithm,
                fiscal.TimeFormat, fiscal.PublicKey, fiscal.ProcessType, fiscal.ProcessData, fiscal.QrPayload),
            receipt.Customer,
            receipt.RoundingMinor,
            receipt.Offline is { } recorded ? new DocumentOffline(recorded.RecordedAt, recorded.Reference) : null);
    }

    private static readonly TimeZoneInfo Berlin = FindBerlin();

    /// <summary>Printed on receipts of sales recorded offline: local (Europe/Berlin) recording time and the device reference.</summary>
    public static string OfflineNote(OfflineRecord offline) =>
        $"Offline erfasst am {TimeZoneInfo.ConvertTime(offline.RecordedAt, Berlin).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
        + $" · Offline-Nr. {offline.Reference} · nachträglich gebucht und signiert";

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
