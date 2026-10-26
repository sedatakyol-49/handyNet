using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>The fiscal configuration that applies to a register: register override → organization → <c>Pos:FiscalSigner</c>.</summary>
public sealed record EffectiveFiscalConfiguration(
    FiscalMode? Mode,
    string ModeSource,
    SimulatedTseBehaviour Simulated,
    string ClientId,
    FiscalConfiguration? Organization,
    FiscalConfiguration? Register);

/// <summary>A signer ready for one register.</summary>
/// <summary>IsTest: simulated TSE or fiskaly not configured as Production (receipts are marked as test receipts).</summary>
public sealed record ResolvedFiscalSigner(IFiscalSigner Signer, FiscalMode Mode, string ClientId, bool IsTest = false);

/// <summary>Resolves the fiscal mode and signer per organization/register, including the Production gates.</summary>
internal sealed class FiscalSignerProvider(
    HandyNetDbContext db,
    ITenantContext tenant,
    IOptions<PosOptions> options,
    IHostEnvironment environment,
    IServiceProvider services,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<EffectiveFiscalConfiguration> EffectiveAsync(Guid? registerId, CancellationToken ct)
    {
        var rows = await db.Set<FiscalConfiguration>().AsNoTracking()
            .Where(c => c.OrganizationId == OrganizationId && (c.RegisterId == null || c.RegisterId == registerId))
            .ToListAsync(ct);
        var org = rows.FirstOrDefault(c => c.RegisterId == null);
        var register = registerId is null ? null : rows.FirstOrDefault(c => c.RegisterId == registerId);
        var mode = register?.Mode ?? org?.Mode ?? options.Value.FiscalSigner;
        var source = register?.Mode is not null ? "Register" : org?.Mode is not null ? "Organization" : mode is not null ? "Default" : "None";
        var simulated = new SimulatedTseBehaviour(
            register?.SimulatedLatencyMs ?? org?.SimulatedLatencyMs ?? 0,
            register?.SimulatedFailureRatePercent ?? org?.SimulatedFailureRatePercent ?? 0,
            register?.SimulatedOutage ?? org?.SimulatedOutage ?? false,
            org?.SimulatedAlgorithm ?? KassenSichV.AlgorithmP256);
        var clientId = register?.ClientId ?? (registerId is { } id ? DefaultClientId(mode, id) : "");
        return new EffectiveFiscalConfiguration(mode, source, simulated, clientId, org, register);
    }

    /// <summary>The fiskaly client id must be a UUIDv4 (derived from the register id); the simulated TSE uses the register id as Kassen-Seriennummer.</summary>
    public static string DefaultClientId(FiscalMode? mode, Guid registerId) =>
        mode == FiscalMode.FiskalyCloudTse ? FiskalyIds.Client(registerId).ToString() : registerId.ToString("N");

    /// <summary>Null when signing is possible for the register (or the organization), otherwise the reason (409/503-like conflict).</summary>
    public async Task<Error?> CheckAsync(Guid? registerId, CancellationToken ct)
    {
        var effective = await EffectiveAsync(registerId, ct);
        return Gate(effective.Mode);
    }

    public Error? Gate(FiscalMode? mode) => mode switch
    {
        null => Error.Conflict("pos.fiscal_signer_missing", "No fiscal signer (TSE) is configured. POS sales are not possible."),
        FiscalMode.Disabled when environment.IsProduction() =>
            Error.Conflict("pos.fiscal_disabled_in_production", "Without a TSE the till cannot be used in production."),
        FiscalMode.SimulatedTse when environment.IsProduction() && !options.Value.Fiscal.AllowSimulatedTseInProduction =>
            Error.Conflict("pos.fiscal_simulated_not_allowed", "The simulated test TSE must not be used in production."),
        _ => null,
    };

    public async Task<Result<ResolvedFiscalSigner>> ResolveAsync(Guid registerId, CancellationToken ct)
    {
        var effective = await EffectiveAsync(registerId, ct);
        if (Gate(effective.Mode) is { } refused)
            return refused;
        IFiscalSigner signer;
        switch (effective.Mode!.Value)
        {
            case FiscalMode.Disabled:
                signer = new DisabledFiscalSigner();
                break;
            case FiscalMode.SimulatedTse:
                signer = new SimulatedTseSigner(services.GetRequiredService<ISimulatedTseStore>(), services.GetRequiredService<IFiscalSecretProtector>(),
                    services.GetRequiredService<ISimulatedTseFaults>(), clock, OrganizationId, effective.Simulated);
                break;
            case FiscalMode.FiskalyCloudTse:
                var credentials = FiskalyCredentialsOf(effective.Organization);
                if (effective.Organization?.FiskalyTssId is not { } tss || credentials is null)
                    return Error.Conflict("pos.fiscal_configuration_incomplete",
                        "The fiskaly TSE is not fully configured (TSS id and credentials are required).");
                signer = new FiskalyTseSigner(services.GetRequiredService<FiskalyApiClient>(), credentials, tss,
                    effective.Organization.FiskalyEnvironment != "Production");
                break;
            default:
                return Error.Conflict("pos.fiscal_signer_missing", "Unknown fiscal mode.");
        }
        var isTest = effective.Mode == FiscalMode.SimulatedTse
                     || (effective.Mode == FiscalMode.FiskalyCloudTse && effective.Organization?.FiskalyEnvironment != "Production");
        return new ResolvedFiscalSigner(signer, effective.Mode.Value, effective.ClientId, isTest);
    }

    internal FiskalyCredentials? FiskalyCredentialsOf(FiscalConfiguration? config)
    {
        if (config is null)
            return null;
        if (config.FiskalyCredentialReference is { } reference)
            return options.Value.Fiskaly.Credentials.TryGetValue(reference, out var named) && !string.IsNullOrEmpty(named.ApiKey) && !string.IsNullOrEmpty(named.ApiSecret)
                ? new FiskalyCredentials(named.ApiKey, named.ApiSecret)
                : null;
        if (config.FiskalyApiKeyProtected is null || config.FiskalyApiSecretProtected is null)
            return null;
        var protector = services.GetRequiredService<IFiscalSecretProtector>();
        var key = protector.Unprotect(config.FiskalyApiKeyProtected);
        var secret = protector.Unprotect(config.FiskalyApiSecretProtected);
        return key is null || secret is null ? null : new FiskalyCredentials(key, secret);
    }
}

/// <summary>
/// Signs receipts and other till events with the configured TSE: start and finish with retries (same idempotency key,
/// so a retry never signs twice), and — when the TSE stays unavailable — the KassenSichV outage path: the sale still
/// completes, the receipt carries "Sicherheitseinrichtung ausgefallen" and the outage is recorded per register (start
/// with the first failure, end with the next successful signature).
/// </summary>
internal sealed class FiscalSealer(
    FiscalSignerProvider provider,
    HandyNetDbContext db,
    ITenantContext tenant,
    IClock clock,
    IOptions<PosOptions> options,
    ILogger<FiscalSealer> logger)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public Task<Error?> CheckAsync(Guid? registerId, CancellationToken ct) => provider.CheckAsync(registerId, ct);

    /// <summary>Signs a sale, storno or return (processType Kassenbeleg-V1); the receipt id is the transaction key.</summary>
    public async Task<Result<FiscalData>> SignReceiptAsync(Receipt receipt, CancellationToken ct)
    {
        var resolved = await provider.ResolveAsync(receipt.RegisterId, ct);
        if (!resolved.IsSuccess)
            return resolved.Error!;
        var summary = SessionCalculator.Summarize(0, [receipt], []);
        var data = new FiscalReceiptData(receipt.Kind, receipt.Number, receipt.TotalGrossMinor, summary.ByTaxTreatment, summary.ByPaymentMethod);
        var process = new FiscalProcess(KassenSichV.ProcessTypeReceipt,
            KassenSichV.ReceiptProcessData(summary.ByTaxTreatment, summary.ByPaymentMethod, receipt.Currency), data);
        return await SignAsync(resolved.Value, receipt.RegisterId, receipt.SessionId, receipt.Id, process, ct);
    }

    /// <summary>
    /// Signs a non-sale event (processType SonstigerVorgang) and adds its <see cref="FiscalRecord"/> to the unit of work.
    /// Null when no TSE is in use (Disabled, or not configured) — then nothing is recorded and the event is not blocked.
    /// </summary>
    public async Task<FiscalRecord?> SignOtherAsync(Guid registerId, Guid sessionId, FiscalRecordKind kind, Guid referenceId, string processData,
        CancellationToken ct)
    {
        var resolved = await provider.ResolveAsync(registerId, ct);
        if (!resolved.IsSuccess)
        {
            if (resolved.Error!.Code != "pos.fiscal_signer_missing")
                logger.LogWarning("{Kind} {Reference} not signed: {Code}", kind, referenceId, resolved.Error.Code);
            return null;
        }
        if (resolved.Value.Mode == FiscalMode.Disabled)
            return null;
        var data = await SignAsync(resolved.Value, registerId, sessionId, referenceId, new FiscalProcess(KassenSichV.ProcessTypeOther, processData), ct);
        if (!data.IsSuccess)
            return null;
        var record = FiscalRecord.For(OrganizationId, registerId, sessionId, kind, referenceId, data.Value, clock.UtcNow);
        db.Add(record);
        return record;
    }

    private async Task<Result<FiscalData>> SignAsync(ResolvedFiscalSigner resolved, Guid registerId, Guid? sessionId, Guid key, FiscalProcess process,
        CancellationToken ct)
    {
        var signer = resolved.Signer;
        var request = new FiscalTransactionRequest(OrganizationId, registerId, key, resolved.ClientId, process.ProcessType);
        var attempts = Math.Max(1, options.Value.Fiscal.MaxAttempts);

        Error? last = null;
        FiscalTransaction? transaction = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var started = await signer.StartTransactionAsync(request, ct);
            if (started.IsSuccess)
            {
                transaction = started.Value;
                break;
            }
            last = started.Error!;
            if (!FiscalErrors.IsTransient(last) || attempt == attempts)
                break;
            await PauseAsync(attempt, ct);
        }

        if (transaction is not null)
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var finished = await signer.FinishTransactionAsync(transaction, process, ct);
                if (finished.IsSuccess)
                {
                    if (resolved.Mode != FiscalMode.Disabled)
                        await EndOutageAsync(registerId, ct);
                    return finished.Value;
                }
                last = finished.Error!;
                if (!FiscalErrors.IsTransient(last) || attempt == attempts)
                    break;
                await PauseAsync(attempt, ct);
            }
        }

        if (resolved.Mode == FiscalMode.Disabled)
            return last!;
        logger.LogWarning("TSE outage on register {RegisterId} ({Mode}): {Code} {Message}", registerId, resolved.Mode, last!.Code, last.Message);
        await StartOutageAsync(registerId, sessionId, resolved.Mode, last.Message, ct);
        return new FiscalData(FiscalStatus.Failed, KassenSichV.OutageNotice, transaction?.TseSerialNumber, transaction?.Number, null, null,
            transaction?.StartedAt, null, Mode: resolved.Mode.ToString(), IsTest: resolved.IsTest, ClientId: resolved.ClientId,
            ProcessType: process.ProcessType, ProcessData: process.ProcessData, Error: Truncate(last.Message, 500));
    }

    private Task PauseAsync(int attempt, CancellationToken ct) =>
        options.Value.Fiscal.RetryDelayMs > 0 ? Task.Delay(options.Value.Fiscal.RetryDelayMs * attempt, ct) : Task.CompletedTask;

    private async Task StartOutageAsync(Guid registerId, Guid? sessionId, FiscalMode mode, string reason, CancellationToken ct)
    {
        if (await OpenOutageAsync(registerId, ct) is not null)
            return;
        db.Add(FiscalOutage.Start(OrganizationId, registerId, sessionId, mode, reason, clock.UtcNow));
    }

    private async Task EndOutageAsync(Guid registerId, CancellationToken ct)
    {
        if (await OpenOutageAsync(registerId, ct) is { } open)
            open.End(clock.UtcNow);
    }

    /// <summary>The register's open outage (tracked; also one added earlier in this unit of work).</summary>
    private async Task<FiscalOutage?> OpenOutageAsync(Guid registerId, CancellationToken ct) =>
        db.Set<FiscalOutage>().Local.FirstOrDefault(o => o.RegisterId == registerId && o.EndedAt is null)
        ?? await db.Set<FiscalOutage>().FirstOrDefaultAsync(o => o.OrganizationId == OrganizationId && o.RegisterId == registerId && o.EndedAt == null, ct);

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
