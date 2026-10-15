using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>
/// Test operation without a TSE. Produces no signature, no transaction number and no TSE timestamps — only the clear
/// notice that no TSE is connected. Refused at startup in Production (see <c>PosModule</c>).
/// </summary>
public sealed class DisabledFiscalSigner : IFiscalSigner
{
    public FiscalSignerKind Kind => FiscalSignerKind.Disabled;

    public Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct) =>
        Task.FromResult<Result<FiscalTransaction>>(new FiscalTransaction(null, null));

    public Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalReceiptData data, CancellationToken ct) =>
        Task.FromResult<Result<FiscalData>>(new FiscalData(FiscalStatus.NotConnected, ReceiptDocument.NotConnectedNotice,
            null, null, null, null, null, null));
}
