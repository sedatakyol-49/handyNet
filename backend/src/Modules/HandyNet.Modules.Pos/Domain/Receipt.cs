using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum ReceiptKind
{
    Sale,

    /// <summary>Cancels a whole sale receipt: exact negative mirror, stock back.</summary>
    Storno,

    /// <summary>Customer returns selected lines/quantities of a sale.</summary>
    Return,

    /// <summary>
    /// Payment of a sales invoice at the till ("An der Kasse kassieren"): one not taxable line, no stock movement (the
    /// invoice already moved the stock), signed like any receipt; references the invoice (<see cref="Receipt.SalesInvoiceId"/>).
    /// </summary>
    InvoicePayment,
}

/// <summary>Stored as text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PaymentMethod>))]
public enum PaymentMethod
{
    Cash,

    /// <summary>Card payment confirmed on a separate terminal (no terminal integration yet).</summary>
    Card,
}

/// <summary>Stored as text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FiscalStatus>))]
public enum FiscalStatus
{
    /// <summary>No technical security device (TSE) is connected: test operation, no signature exists.</summary>
    NotConnected,

    /// <summary>Signed by a TSE (the simulated test TSE or the fiskaly adapter; see <see cref="FiscalData.IsTest"/>).</summary>
    Signed,

    /// <summary>
    /// The TSE failed (outage, "Sicherheitseinrichtung ausgefallen"): the receipt was issued without a signature, carries the
    /// outage notice, and the outage is recorded (<c>pos.fiscal_outages</c>).
    /// </summary>
    Failed,
}

/// <summary>
/// Result of the fiscal signer for one receipt or other signed event. Every field but the status may be null; nothing is
/// ever invented. Mode: the <see cref="Domain.FiscalMode"/> that produced it (as text); IsTest: produced by the simulated
/// test TSE (never valid for real operation); ClientId: the Kassen-Seriennummer of the QR code; ProcessType/ProcessData:
/// what the TSE signed; QrPayload: the DSFinV-K QR code content; Error: why the TSE failed (outage).
/// </summary>
public sealed record FiscalData(
    FiscalStatus Status,
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
    string? ProcessType = null,
    string? ProcessData = null,
    string? SignatureAlgorithm = null,
    string? TimeFormat = null,
    string? PublicKey = null,
    string? QrPayload = null,
    string? Error = null);

public sealed record SaleLineInput(
    Guid ProductId,
    Guid? StockUnitId,
    string? Imei,
    string Description,
    int Quantity,
    long UnitPriceMinor,
    long DiscountMinor,
    TaxTreatment Treatment,
    long? PurchasePriceMinor,
    string? Note = null);

public sealed record PaymentInput(PaymentMethod Method, long AmountMinor);

/// <summary>Where and when a receipt is issued. The number was allocated from the register in the same transaction.</summary>
public sealed record ReceiptHeader(
    Guid OrganizationId,
    Guid RegisterId,
    Guid SessionId,
    Guid WarehouseId,
    long Number,
    Guid UserId,
    DateTimeOffset Now,
    string? IdempotencyKey = null,
    string? RequestHash = null,
    DocumentCustomer? Customer = null);

/// <summary>
/// A till receipt (Kassenbeleg). Immutable once written (GoBD): the application role has no UPDATE/DELETE grant and a
/// trigger refuses changes even for the owner. Corrections are new negative receipts (Storno, Return) that reference
/// the original. Amounts are signed cents: sales positive, refunds negative.
/// </summary>
public sealed class Receipt
{
    public const int MaxLines = 200;
    public const int MaxQuantity = 10_000;
    public const int MaxReasonLength = 500;
    public const int MaxDescriptionLength = 300;
    public const int MaxNoteLength = 200;

    private readonly List<ReceiptLine> _lines = [];
    private readonly List<ReceiptPayment> _payments = [];

    private Receipt() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public Guid SessionId { get; private set; }

    /// <summary>The register's warehouse at the time of the sale: returns go back here.</summary>
    public Guid WarehouseId { get; private set; }
    public long Number { get; private set; }
    public ReceiptKind Kind { get; private set; }
    public Guid? ReferencedReceiptId { get; private set; }

    /// <summary>InvoicePayment receipts: the sales invoice paid at the till.</summary>
    public Guid? SalesInvoiceId { get; private set; }

    /// <summary>Optional POS customer. Storno and return receipts carry the customer of the original sale.</summary>
    public Guid? CustomerId { get; private set; }

    private DocumentCustomer? _customer;

    /// <summary>The customer as printed (snapshot at issue time). Stored only inside <see cref="Document"/>.</summary>
    public DocumentCustomer? Customer => Document?.Customer ?? _customer;
    public string? IdempotencyKey { get; private set; }
    public string? RequestHash { get; private set; }
    public string Currency { get; private set; } = null!;
    public long TotalGrossMinor { get; private set; }
    public long TotalNetMinor { get; private set; }

    /// <summary>All VAT owed, including §25a margin VAT (which is not printed on the receipt).</summary>
    public long TotalVatMinor { get; private set; }
    public long ChangeMinor { get; private set; }

    /// <summary>
    /// Cash rounding (Rappenrundung) on registers that round cash totals to 0.05 €: payments = total + rounding. Signed
    /// cents between −2 and +2; 0 without rounding. Lines and VAT are never rounded.
    /// </summary>
    public long RoundingMinor { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    public FiscalStatus? FiscalStatus { get; private set; }
    public string? FiscalNotice { get; private set; }
    public string? FiscalTseSerialNumber { get; private set; }
    public long? FiscalTransactionNumber { get; private set; }
    public long? FiscalSignatureCounter { get; private set; }
    public string? FiscalSignature { get; private set; }
    public DateTimeOffset? FiscalStartedAt { get; private set; }
    public DateTimeOffset? FiscalFinishedAt { get; private set; }
    public string? FiscalMode { get; private set; }
    public bool? FiscalIsTest { get; private set; }
    public string? FiscalClientId { get; private set; }
    public string? FiscalProcessType { get; private set; }
    public string? FiscalProcessData { get; private set; }
    public string? FiscalSignatureAlgorithm { get; private set; }
    public string? FiscalTimeFormat { get; private set; }
    public string? FiscalPublicKey { get; private set; }
    public string? FiscalQrPayload { get; private set; }
    public string? FiscalError { get; private set; }

    /// <summary>Printable receipt as issued (seller, lines, VAT breakdown, payments, fiscal data).</summary>
    public ReceiptDocument? Document { get; private set; }

    public IReadOnlyList<ReceiptLine> Lines => _lines;
    public IReadOnlyList<ReceiptPayment> Payments => _payments;

    public bool IsSealed => FiscalStatus is not null && Document is not null;

    /// <param name="cashRounding">The register rounds cash amounts to 0.05 € (see <see cref="CashRounding"/>).</param>
    public static Result<Receipt> CreateSale(ReceiptHeader header, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<PaymentInput> payments,
        bool cashRounding = false)
    {
        if (lines.Count == 0)
            return Error.Validation("pos.empty_sale", "A sale needs at least one line.");
        if (lines.Count > MaxLines)
            return Error.Validation("pos.too_many_lines", $"At most {MaxLines} lines per receipt.");
        var unitIds = lines.Where(l => l.StockUnitId is not null).Select(l => l.StockUnitId).ToList();
        if (unitIds.Distinct().Count() != unitIds.Count)
            return Error.Validation("pos.duplicate_unit", "The same device appears twice in the sale.");

        var receipt = New(header, ReceiptKind.Sale, null, null);
        var lineNumber = 0;
        foreach (var input in lines)
        {
            var line = ReceiptLine.ForSale(receipt, ++lineNumber, input);
            if (!line.IsSuccess)
                return line.Error!;
            receipt._lines.Add(line.Value);
        }

        receipt.SumLines();
        if (receipt.TotalGrossMinor <= 0)
            return Error.Validation("pos.total_not_positive", "The receipt total must be positive.");

        var settled = receipt.SettleSale(payments, cashRounding);
        return settled.IsSuccess ? receipt : settled.Error!;
    }

    public const string InvoicePaymentPrefix = "Zahlung Rechnung ";

    /// <summary>
    /// Payment of a sales invoice at the till: one not taxable line ("Zahlung Rechnung RE-…") over the amount applied to the
    /// invoice, settled like a sale (cash with change and rounding, card exact). No product, no stock.
    /// </summary>
    public static Result<Receipt> CreateInvoicePayment(ReceiptHeader header, Guid salesInvoiceId, string invoiceNumber, long amountMinor,
        IReadOnlyList<PaymentInput> payments, bool cashRounding = false)
    {
        if (amountMinor <= 0)
            return Error.Validation("pos.payment_amount", "The amount to collect must be positive.");
        var receipt = New(header, ReceiptKind.InvoicePayment, null, null);
        receipt.SalesInvoiceId = salesInvoiceId;
        receipt._lines.Add(ReceiptLine.InvoicePayment(receipt, invoiceNumber, amountMinor));
        receipt.SumLines();
        var settled = receipt.SettleSale(payments, cashRounding);
        return settled.IsSuccess ? receipt : settled.Error!;
    }

    /// <summary>Cancels a whole sale: every line and payment mirrored exactly. Not allowed once anything was returned.</summary>
    public static Result<Receipt> CreateStorno(ReceiptHeader header, Receipt original, bool hasReturns, string reason)
    {
        if (original.Kind != ReceiptKind.Sale)
            return Error.Conflict("pos.not_a_sale", "Only sale receipts can be cancelled.");
        if (original.OrganizationId != header.OrganizationId)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        if (hasReturns)
            return Error.Conflict("pos.has_returns", "Items of this receipt were already returned. Return the remaining items instead.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"A reason (max {MaxReasonLength} characters) is required.");

        var receipt = New(header, ReceiptKind.Storno, original.Id, reason.Trim());
        receipt.CopyCustomerOf(original);
        foreach (var line in original.Lines)
            receipt._lines.Add(ReceiptLine.Refund(receipt, line.LineNumber, line, alreadyReturned: 0, quantity: line.Quantity));
        receipt.SumLines();
        foreach (var payment in original.Payments)
            receipt._payments.Add(new ReceiptPayment(receipt, payment.Method, -payment.AmountMinor, null));
        receipt.RoundingMinor = -original.RoundingMinor;
        return receipt;
    }

    /// <summary>
    /// Returns selected quantities of a sale's lines. <paramref name="alreadyReturned"/> holds the quantity per original line
    /// number returned by earlier receipts; more than was sold can never come back. Refunded with one payment method.
    /// </summary>
    public static Result<Receipt> CreateReturn(ReceiptHeader header, Receipt original, IReadOnlyDictionary<int, int> quantities,
        IReadOnlyDictionary<int, int> alreadyReturned, PaymentMethod refundMethod, string reason, bool cashRounding = false)
    {
        if (original.Kind != ReceiptKind.Sale)
            return Error.Conflict("pos.not_a_sale", "Only items of sale receipts can be returned.");
        if (original.OrganizationId != header.OrganizationId)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        if (quantities.Count == 0)
            return Error.Validation("pos.empty_return", "Select at least one line to return.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"A reason (max {MaxReasonLength} characters) is required.");

        var receipt = New(header, ReceiptKind.Return, original.Id, reason.Trim());
        receipt.CopyCustomerOf(original);
        var lineNumber = 0;
        foreach (var (originalLineNumber, quantity) in quantities.OrderBy(q => q.Key))
        {
            var line = original.Lines.FirstOrDefault(l => l.LineNumber == originalLineNumber);
            if (line is null)
                return Error.Validation("pos.line_not_found", $"Line {originalLineNumber} does not exist on the receipt.");
            var returned = alreadyReturned.GetValueOrDefault(originalLineNumber);
            if (quantity <= 0 || returned + quantity > line.Quantity)
                return Error.Conflict("pos.return_exceeds_sold",
                        $"Line {originalLineNumber}: {line.Quantity} sold, {returned} already returned — {quantity} cannot be returned.")
                    .WithDetails(new Dictionary<string, object?>
                    {
                        ["lineNumber"] = originalLineNumber,
                        ["soldQuantity"] = line.Quantity,
                        ["returnedQuantity"] = returned,
                    });
            receipt._lines.Add(ReceiptLine.Refund(receipt, ++lineNumber, line, returned, quantity));
        }

        receipt.SumLines();
        if (receipt.TotalGrossMinor < 0)
        {
            // A cash refund on a rounding register pays out the amount rounded to 0.05 €.
            var refund = cashRounding && refundMethod == PaymentMethod.Cash ? -CashRounding.Round(-receipt.TotalGrossMinor) : receipt.TotalGrossMinor;
            if (refund != 0)
                receipt._payments.Add(new ReceiptPayment(receipt, refundMethod, refund, null));
            receipt.RoundingMinor = refund - receipt.TotalGrossMinor;
        }
        return receipt;
    }

    /// <summary>Adds the fiscal result and the printable document. Only once, before the receipt is written.</summary>
    public Result Seal(FiscalData fiscal, ReceiptDocument document)
    {
        if (IsSealed)
            return Error.Conflict("pos.receipt_sealed", "The receipt is already issued and cannot be changed.");
        FiscalStatus = fiscal.Status;
        FiscalNotice = fiscal.Notice;
        FiscalTseSerialNumber = fiscal.TseSerialNumber;
        FiscalTransactionNumber = fiscal.TransactionNumber;
        FiscalSignatureCounter = fiscal.SignatureCounter;
        FiscalSignature = fiscal.Signature;
        FiscalStartedAt = fiscal.StartedAt;
        FiscalFinishedAt = fiscal.FinishedAt;
        FiscalMode = fiscal.Mode;
        FiscalIsTest = fiscal.Mode is null ? null : fiscal.IsTest;
        FiscalClientId = fiscal.ClientId;
        FiscalProcessType = fiscal.ProcessType;
        FiscalProcessData = fiscal.ProcessData;
        FiscalSignatureAlgorithm = fiscal.SignatureAlgorithm;
        FiscalTimeFormat = fiscal.TimeFormat;
        FiscalPublicKey = fiscal.PublicKey;
        FiscalQrPayload = fiscal.QrPayload;
        FiscalError = fiscal.Error;
        Document = document;
        return Result.Success();
    }

    public FiscalData? Fiscal => FiscalStatus is { } status
        ? new FiscalData(status, FiscalNotice, FiscalTseSerialNumber, FiscalTransactionNumber, FiscalSignatureCounter, FiscalSignature,
            FiscalStartedAt, FiscalFinishedAt, FiscalMode, FiscalIsTest ?? false, FiscalClientId, FiscalProcessType, FiscalProcessData,
            FiscalSignatureAlgorithm, FiscalTimeFormat, FiscalPublicKey, FiscalQrPayload, FiscalError)
        : null;

    private static Receipt New(ReceiptHeader h, ReceiptKind kind, Guid? referenced, string? reason) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = h.OrganizationId,
        RegisterId = h.RegisterId,
        SessionId = h.SessionId,
        WarehouseId = h.WarehouseId,
        Number = h.Number,
        Kind = kind,
        ReferencedReceiptId = referenced,
        IdempotencyKey = h.IdempotencyKey,
        RequestHash = h.RequestHash,
        Currency = Money.Eur,
        Reason = reason,
        CreatedAt = h.Now,
        CreatedBy = h.UserId,
        CustomerId = h.Customer?.Id,
        _customer = h.Customer,
    };

    private void CopyCustomerOf(Receipt original)
    {
        CustomerId = original.CustomerId;
        _customer = original.Customer;
    }

    private void SumLines()
    {
        TotalGrossMinor = checked(_lines.Sum(l => l.LineGrossMinor));
        TotalVatMinor = checked(_lines.Sum(l => l.VatMinor));
        TotalNetMinor = TotalGrossMinor - TotalVatMinor;
    }

    /// <summary>
    /// Card payments may not exceed the total; cash may (the difference is change). Stored payments are the amounts
    /// applied to the receipt; for cash also the amount tendered.
    /// </summary>
    private Result SettleSale(IReadOnlyList<PaymentInput> payments, bool cashRounding)
    {
        if (payments.Any(p => p.AmountMinor <= 0))
            return Error.Validation("pos.payment_amount", "Payment amounts must be positive.");
        var cash = checked(payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountMinor));
        var card = checked(payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.AmountMinor));
        if (card > TotalGrossMinor)
            return Error.Validation("pos.card_exceeds_total", "A card payment cannot exceed the receipt total.");
        var paid = checked(cash + card);
        // Cash rounding: the part paid in cash (total − card) is rounded to 0.05 €; card amounts are exact.
        var target = TotalGrossMinor;
        if (cashRounding && cash > 0 && CashRounding.Round(TotalGrossMinor - card) is > 0 and var roundedDue)
            target = card + roundedDue;
        if (paid < target)
            return Error.Validation("pos.payment_insufficient",
                    $"Payments ({paid / 100m:0.00} €) do not cover the total ({target / 100m:0.00} €).")
                .WithDetails(new Dictionary<string, object?> { ["totalGrossMinor"] = TotalGrossMinor, ["paidMinor"] = paid, ["amountDueMinor"] = target });

        RoundingMinor = target - TotalGrossMinor;
        var change = paid - target;
        var cashApplied = cash - change;
        if (cash > 0 && cashApplied <= 0)
            return Error.Validation("pos.cash_not_needed", "The card payment already covers the total; no cash is needed.");

        ChangeMinor = change;
        if (cashApplied > 0)
            _payments.Add(new ReceiptPayment(this, PaymentMethod.Cash, cashApplied, cash));
        if (card > 0)
            _payments.Add(new ReceiptPayment(this, PaymentMethod.Card, card, null));
        return Result.Success();
    }
}

public sealed class ReceiptLine
{
    private ReceiptLine() { }

    public Guid OrganizationId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public int LineNumber { get; private set; }

    /// <summary>Null only on the not taxable line of an invoice payment receipt.</summary>
    public Guid? ProductId { get; private set; }
    public Guid? StockUnitId { get; private set; }
    public string? Imei { get; private set; }
    public string Description { get; private set; } = null!;

    /// <summary>Signed: positive on sales, negative on refunds.</summary>
    public int Quantity { get; private set; }
    public long UnitPriceMinor { get; private set; }

    /// <summary>Invariant (CHECK constraint): line_gross = unit_price × quantity − discount.</summary>
    public long DiscountMinor { get; private set; }
    public long LineGrossMinor { get; private set; }
    public TaxTreatment TaxTreatment { get; private set; }
    public int VatRateBasisPoints { get; private set; }
    public long VatMinor { get; private set; }
    public long NetMinor { get; private set; }

    /// <summary>§25a only: the unit's purchase price (internal; never printed).</summary>
    public long? PurchasePriceMinor { get; private set; }

    /// <summary>Refund lines: the line of the original sale receipt.</summary>
    public int? OriginalLineNumber { get; private set; }

    /// <summary>Free-text line note (e.g. "Displayschutz montiert"), printed under the line. Immutable with the receipt.</summary>
    public string? Note { get; private set; }

    internal static Result<ReceiptLine> ForSale(Receipt receipt, int lineNumber, SaleLineInput input)
    {
        if (input.Quantity is <= 0 or > Receipt.MaxQuantity)
            return Error.Validation("pos.quantity", $"Line {lineNumber}: quantity must be between 1 and {Receipt.MaxQuantity}.");
        if (input.UnitPriceMinor <= 0)
            return Error.Validation("pos.unit_price", $"Line {lineNumber}: the price must be positive.");
        var gross = checked(input.UnitPriceMinor * input.Quantity);
        if (input.DiscountMinor < 0 || input.DiscountMinor > gross)
            return Error.Validation("pos.discount", $"Line {lineNumber}: the discount must be between 0 and the line amount.");
        if (input.Treatment == TaxTreatment.Margin25a && (input.Quantity != 1 || input.PurchasePriceMinor is not >= 0 || input.StockUnitId is null))
            return Error.Validation("pos.margin_line", $"Line {lineNumber}: §25a applies to a single serialized device with a known purchase price.");
        if (input.StockUnitId is not null && input.Quantity != 1)
            return Error.Validation("pos.quantity", $"Line {lineNumber}: a serialized device is sold with quantity 1.");
        if (input.Note?.Trim().Length > Receipt.MaxNoteLength)
            return Error.Validation("pos.line_note", $"Line {lineNumber}: the note is limited to {Receipt.MaxNoteLength} characters.");
        if (string.IsNullOrWhiteSpace(input.Description))
            return Error.Validation("pos.description", $"Line {lineNumber}: a description is required.");

        var lineGross = gross - input.DiscountMinor;
        var vat = VatCalculator.VatFor(input.Treatment, lineGross, input.PurchasePriceMinor);
        var description = input.Description.Trim();
        return new ReceiptLine
        {
            OrganizationId = receipt.OrganizationId,
            ReceiptId = receipt.Id,
            LineNumber = lineNumber,
            ProductId = input.ProductId,
            StockUnitId = input.StockUnitId,
            Imei = input.Imei,
            Description = description.Length > Receipt.MaxDescriptionLength ? description[..Receipt.MaxDescriptionLength] : description,
            Quantity = input.Quantity,
            UnitPriceMinor = input.UnitPriceMinor,
            DiscountMinor = input.DiscountMinor,
            LineGrossMinor = lineGross,
            TaxTreatment = input.Treatment,
            VatRateBasisPoints = VatCalculator.RateFor(input.Treatment),
            VatMinor = vat,
            NetMinor = lineGross - vat,
            PurchasePriceMinor = input.Treatment == TaxTreatment.Margin25a ? input.PurchasePriceMinor : null,
            Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
        };
    }

    internal static ReceiptLine InvoicePayment(Receipt receipt, string invoiceNumber, long amountMinor) => new()
    {
        OrganizationId = receipt.OrganizationId,
        ReceiptId = receipt.Id,
        LineNumber = 1,
        ProductId = null,
        Description = Receipt.InvoicePaymentPrefix + invoiceNumber,
        Quantity = 1,
        UnitPriceMinor = amountMinor,
        DiscountMinor = 0,
        LineGrossMinor = amountMinor,
        TaxTreatment = TaxTreatment.NotTaxable,
        VatRateBasisPoints = 0,
        VatMinor = 0,
        NetMinor = amountMinor,
    };

    /// <summary>
    /// Negative mirror of units (alreadyReturned+1 … alreadyReturned+quantity) of a sale line. Gross and VAT are allocated
    /// so that all partial refunds of a line add up to exactly the original line.
    /// </summary>
    internal static ReceiptLine Refund(Receipt receipt, int lineNumber, ReceiptLine original, int alreadyReturned, int quantity)
    {
        var to = alreadyReturned + quantity;
        var gross = VatCalculator.Allocate(original.LineGrossMinor, to, original.Quantity)
                    - VatCalculator.Allocate(original.LineGrossMinor, alreadyReturned, original.Quantity);
        var vat = VatCalculator.Allocate(original.VatMinor, to, original.Quantity)
                  - VatCalculator.Allocate(original.VatMinor, alreadyReturned, original.Quantity);
        var discount = checked(original.UnitPriceMinor * quantity) - gross;
        return new ReceiptLine
        {
            OrganizationId = receipt.OrganizationId,
            ReceiptId = receipt.Id,
            LineNumber = lineNumber,
            ProductId = original.ProductId,
            StockUnitId = original.StockUnitId,
            Imei = original.Imei,
            Description = original.Description,
            Quantity = -quantity,
            UnitPriceMinor = original.UnitPriceMinor,
            DiscountMinor = -discount,
            LineGrossMinor = -gross,
            TaxTreatment = original.TaxTreatment,
            VatRateBasisPoints = original.VatRateBasisPoints,
            VatMinor = -vat,
            NetMinor = -(gross - vat),
            PurchasePriceMinor = original.PurchasePriceMinor,
            OriginalLineNumber = original.LineNumber,
            Note = original.Note,
        };
    }
}

public sealed class ReceiptPayment
{
    private ReceiptPayment() { }

    internal ReceiptPayment(Receipt receipt, PaymentMethod method, long amountMinor, long? tenderedMinor)
    {
        OrganizationId = receipt.OrganizationId;
        ReceiptId = receipt.Id;
        Method = method;
        AmountMinor = amountMinor;
        TenderedMinor = tenderedMinor;
    }

    public Guid OrganizationId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public PaymentMethod Method { get; private set; }

    /// <summary>Applied to the receipt (signed). For cash: tendered − change.</summary>
    public long AmountMinor { get; private set; }

    /// <summary>Cash handed over by the customer (sales only).</summary>
    public long? TenderedMinor { get; private set; }
}

/// <summary>Cash rounding to 0.05 € (Rappenrundung): remainders of 1–2 cents go down, 3–4 cents go up.</summary>
public static class CashRounding
{
    public const long Step = 5;

    /// <summary>Rounds a non-negative cent amount to the nearest 5 cents.</summary>
    public static long Round(long amountMinor)
    {
        if (amountMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(amountMinor));
        var remainder = amountMinor % Step;
        return remainder <= 2 ? amountMinor - remainder : amountMinor + (Step - remainder);
    }
}
