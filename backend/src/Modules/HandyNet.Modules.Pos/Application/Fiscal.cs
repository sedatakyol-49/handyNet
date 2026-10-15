using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Which fiscal signer (TSE adapter, KassenSichV) the POS uses. Only a test signer exists today.</summary>
public enum FiscalSignerKind
{
    /// <summary>
    /// No TSE: receipts are marked "TSE nicht angebunden – Testbetrieb" and carry no signature. Development and tests only;
    /// refused at startup in Production.
    /// </summary>
    Disabled,
}

public sealed class PosOptions
{
    public const string Section = "Pos";

    /// <summary>Not set = POS sales are refused (no signer configured). A real adapter (e.g. fiskaly) is not implemented yet.</summary>
    public FiscalSignerKind? FiscalSigner { get; set; }
}

public sealed record FiscalTransactionRequest(Guid OrganizationId, Guid RegisterId, ReceiptKind Kind, long ReceiptNumber);

/// <summary>An open TSE transaction; null fields when the signer has no TSE.</summary>
public sealed record FiscalTransaction(string? TransactionId, DateTimeOffset? StartedAt);

/// <summary>What a TSE signs at the end of a transaction (DSFinV-K style process data: totals per VAT rate and payment).</summary>
public sealed record FiscalReceiptData(ReceiptKind Kind, long ReceiptNumber, long TotalGrossMinor, IReadOnlyList<TaxTotal> VatTotals,
    IReadOnlyList<PaymentTotal> Payments);

/// <summary>
/// Fiscal signing of receipts (KassenSichV technical security device). Called inside the receipt transaction: start
/// before the receipt is built, finish with the final figures; the result is stored on the immutable receipt. An adapter
/// must never invent signature data — if it has none, it says so.
/// </summary>
public interface IFiscalSigner
{
    FiscalSignerKind Kind { get; }

    Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct);

    Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalReceiptData data, CancellationToken ct);
}
