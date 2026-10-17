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

/// <summary>VAT breakdown as printed: only regularly taxed lines. §25a VAT is never shown on the receipt.</summary>
public sealed record DocumentVat(string Label, int RateBasisPoints, long GrossMinor, long NetMinor, long VatMinor);

public sealed record DocumentPayment(string Method, long AmountMinor, long? TenderedMinor);

public sealed record DocumentFiscal(
    string Status,
    string? Notice,
    string? TseSerialNumber,
    long? TransactionNumber,
    long? SignatureCounter,
    string? Signature,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

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
    DocumentCustomer? Customer = null)
{
    public const string StandardLabel = "A";
    public const string MarginLabel = "§25a";
    public const string MarginSchemeNote =
        "Gebrauchtgegenstände/Sonderregelung (Differenzbesteuerung nach § 25a UStG): Die enthaltene Umsatzsteuer wird nicht gesondert ausgewiesen.";
    public const string NotConnectedNotice = "TSE nicht angebunden – Testbetrieb";

    public static ReceiptDocument For(Receipt receipt, SellerInfo seller, string registerName, long? referencedReceiptNumber, FiscalData fiscal)
    {
        var lines = receipt.Lines.Select(l => new DocumentLine(l.Description, l.Imei, l.Quantity, l.UnitPriceMinor, l.DiscountMinor,
            l.LineGrossMinor, l.TaxTreatment == TaxTreatment.Margin25a ? MarginLabel : StandardLabel, l.Note)).ToList();

        var vat = receipt.Lines.Where(l => l.TaxTreatment == TaxTreatment.Standard19)
            .GroupBy(l => l.VatRateBasisPoints)
            .Select(g => new DocumentVat($"{StandardLabel} {g.Key / 100m:0.##} %", g.Key, g.Sum(l => l.LineGrossMinor), g.Sum(l => l.NetMinor),
                g.Sum(l => l.VatMinor)))
            .ToList();

        var notes = new List<string>();
        if (receipt.Lines.Any(l => l.TaxTreatment == TaxTreatment.Margin25a))
            notes.Add(MarginSchemeNote);
        if (fiscal.Status == FiscalStatus.NotConnected)
            notes.Add(fiscal.Notice ?? NotConnectedNotice);

        var title = receipt.Kind switch
        {
            ReceiptKind.Storno => "Storno-Beleg",
            ReceiptKind.Return => "Rückgabe-Beleg",
            _ => "Kassenbeleg",
        };

        return new ReceiptDocument(title, seller, receipt.RegisterId, registerName, receipt.Number, receipt.Kind.ToString(), referencedReceiptNumber,
            receipt.CreatedAt, receipt.Currency, lines, vat, receipt.TotalGrossMinor,
            receipt.Payments.Select(p => new DocumentPayment(p.Method.ToString(), p.AmountMinor, p.TenderedMinor)).ToList(),
            receipt.ChangeMinor, notes,
            new DocumentFiscal(fiscal.Status.ToString(), fiscal.Notice, fiscal.TseSerialNumber, fiscal.TransactionNumber, fiscal.SignatureCounter,
                fiscal.Signature, fiscal.StartedAt, fiscal.FinishedAt),
            receipt.Customer);
    }
}
