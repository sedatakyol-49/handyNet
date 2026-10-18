using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>
/// Test operation without a TSE. Produces no signature, no transaction number and no TSE timestamps — only the clear
/// notice that no TSE is connected. Refused in Production (startup check and per-organization check).
/// </summary>
public sealed class DisabledFiscalSigner : IFiscalSigner
{
    public FiscalMode Mode => FiscalMode.Disabled;

    public Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct) =>
        Task.FromResult<Result<FiscalTransaction>>(new FiscalTransaction(request.TransactionKey, request.ClientId));

    public Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalProcess process, CancellationToken ct) =>
        Task.FromResult<Result<FiscalData>>(new FiscalData(FiscalStatus.NotConnected, ReceiptDocument.NotConnectedNotice,
            null, null, null, null, null, null, Mode: nameof(FiscalMode.Disabled)));
}
