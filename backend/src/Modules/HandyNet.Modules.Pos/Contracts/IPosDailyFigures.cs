namespace HandyNet.Modules.Pos.Contracts;

public sealed record PosTaxFigure(string Treatment, int VatRateBasisPoints, long GrossMinor, long NetMinor, long VatMinor);

public sealed record PosPaymentFigure(string Method, int Count, long AmountMinor);

public sealed record PosSessionFigure(Guid SessionId, string RegisterName, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    long ExpectedCashMinor, long? CountedCashMinor, long? DifferenceMinor);

/// <summary>
/// The current organization's till figures for a period (receipts by creation time). Amounts in cents, signed (refunds negative).
/// VAT per treatment is the stored receipt VAT (for §25a lines the margin VAT; never per product, ADR-0008). Sales figures
/// cover sale, Storno and return receipts; payments of sales invoices at the till (receipt kind InvoicePayment) are counted
/// separately (the invoice carries the revenue). Sales invoices: issued in the period, net of credit notes (gross).
/// </summary>
public sealed record PosDayFigures(
    int SaleCount,
    int StornoCount,
    int ReturnCount,
    long SalesGrossMinor,
    long RefundsGrossMinor,
    long TotalGrossMinor,
    long TotalNetMinor,
    long TotalVatMinor,
    IReadOnlyList<PosTaxFigure> ByTaxTreatment,
    IReadOnlyList<PosPaymentFigure> ByPaymentMethod,
    long DepositsMinor,
    long WithdrawalsMinor,
    int AnkaufPayoutCount,
    long AnkaufPayoutsMinor,
    int InvoiceCount,
    IReadOnlyList<PosSessionFigure> Sessions,
    int InvoicePaymentCount = 0,
    long InvoicePaymentsMinor = 0,
    int SalesInvoiceCount = 0,
    long SalesInvoicesGrossMinor = 0);

/// <summary>Read API of the POS module for the daily business report (Reporting module).</summary>
public interface IPosDailyFigures
{
    Task<PosDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);
}
