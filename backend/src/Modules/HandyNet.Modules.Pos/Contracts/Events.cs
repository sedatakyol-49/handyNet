using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Contracts;

public sealed record EReceiptLine(string Description, string? Imei, int Quantity, long UnitPriceMinor, long DiscountMinor, long LineGrossMinor, string TaxLabel,
    string? Note);

public sealed record EReceiptVat(string Label, long NetMinor, long VatMinor, long GrossMinor);

public sealed record EReceiptPayment(string Method, long AmountMinor, long? TenderedMinor);

/// <summary>
/// The receipt as printed, for an e-receipt. Contains the seller's details and the receipt itself, never the customer
/// snapshot (the recipient knows who they are) and never §25a purchase prices or margin VAT.
/// </summary>
public sealed record EReceiptContent(
    string Title,
    string SellerName,
    string SellerLegalName,
    IReadOnlyList<string> SellerAddress,
    string? SellerVatId,
    string RegisterName,
    long ReceiptNumber,
    DateTimeOffset IssuedAt,
    string Currency,
    IReadOnlyList<EReceiptLine> Lines,
    IReadOnlyList<EReceiptVat> Vat,
    long TotalGrossMinor,
    IReadOnlyList<EReceiptPayment> Payments,
    long ChangeMinor,
    IReadOnlyList<string> Notes,
    long RoundingMinor = 0);

/// <summary>
/// A cashier asked to send a receipt by email. Raised by POS (outbox, same transaction as the recorded request); the
/// Notifications module renders and sends it. The recipient address is the only personal data in the payload.
/// </summary>
public sealed record ReceiptEmailRequested(Guid RequestId, Guid OrganizationId, Guid ReceiptId, string Recipient, EReceiptContent Content) : IDomainEvent;
