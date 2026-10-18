using FluentValidation;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// Fiscal configuration set by the platform admin. Organization row: Mode is required. Register row: null fields inherit.
/// fiskaly credentials are write-only: either FiskalyApiKey + FiskalyApiSecret (stored encrypted with Data Protection) or a
/// FiskalyCredentialReference to a configuration secret; ClearFiskalyCredentials removes stored ones.
/// </summary>
public sealed record FiscalConfigRequest(
    FiscalMode? Mode,
    int? LatencyMs = null,
    int? FailureRatePercent = null,
    bool? ForcedOutage = null,
    string? Algorithm = null,
    string? ClientId = null,
    Guid? FiskalyTssId = null,
    string? FiskalyEnvironment = null,
    string? FiskalyCredentialReference = null,
    string? FiskalyApiKey = null,
    string? FiskalyApiSecret = null,
    bool ClearFiskalyCredentials = false);

/// <summary>Never contains credentials — only whether encrypted ones are stored.</summary>
public sealed record FiscalConfigDto(
    string? Mode,
    int? LatencyMs,
    int? FailureRatePercent,
    bool? ForcedOutage,
    string? Algorithm,
    string? ClientId,
    Guid? FiskalyTssId,
    string? FiskalyEnvironment,
    string? FiskalyCredentialReference,
    bool HasStoredFiskalyCredentials,
    DateTimeOffset UpdatedAt);

public sealed record TseSignatureDto(long TransactionNumber, long? SignatureCounter, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? Signature, string ClientId, string? ProcessType);

public sealed record SimulatedTseDto(Guid Id, string SerialNumber, string PublicKey, string SignatureAlgorithm, string TimeFormat,
    long TransactionCounter, long SignatureCounter, DateTimeOffset CreatedAt, TseSignatureDto? LastTransaction);

public sealed record FiscalOutageDto(Guid Id, Guid RegisterId, string? RegisterName, Guid? SessionId, string Mode, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Reason);

public sealed record RegisterFiscalDto(Guid RegisterId, string Name, bool IsActive, string? Mode, string ModeSource, string ClientId,
    bool ForcedOutage, int LatencyMs, int FailureRatePercent, bool OpenOutage, FiscalConfigDto? Override);

public sealed record OrganizationFiscalDto(
    Guid OrganizationId,
    string Name,
    string? Mode,
    string ModeSource,
    string? DefaultMode,
    FiscalConfigDto? Configuration,
    IReadOnlyList<RegisterFiscalDto> Registers,
    SimulatedTseDto? SimulatedTse,
    IReadOnlyList<FiscalOutageDto> Outages,
    bool ProductionEnvironment,
    bool SimulatedTseAllowed,
    IReadOnlyList<string> Algorithms);

public sealed record OrganizationFiscalSummaryDto(Guid OrganizationId, string Name, string City, bool IsVerified, string? Mode, string ModeSource,
    int RegisterCount, int OpenOutages, string? TseSerialNumber);

/// <summary>
/// TSE state of a register for the till: State = Ok (real TSE), Test (simulated / sandbox — signed, not for real
/// operation), Outage (the TSE failed; receipts carry the outage marker), Disabled (no TSE, test operation), NotConfigured
/// (sales refused) or Refused (configuration not allowed here, see RefusalCode).
/// </summary>
public sealed record RegisterFiscalStatusDto(
    Guid RegisterId,
    string? Mode,
    string State,
    bool IsTest,
    string? TseSerialNumber,
    string ClientId,
    DateTimeOffset? OutageSince,
    string? OutageReason,
    string? RefusalCode,
    DateTimeOffset? LastSignatureAt);

internal sealed class FiscalConfigValidator : AbstractValidator<FiscalConfigRequest>
{
    public FiscalConfigValidator()
    {
        RuleFor(x => x.Mode).IsInEnum().When(x => x.Mode is not null);
        RuleFor(x => x.LatencyMs).InclusiveBetween(0, FiscalConfiguration.MaxLatencyMs).When(x => x.LatencyMs is not null);
        RuleFor(x => x.FailureRatePercent).InclusiveBetween(0, 100).When(x => x.FailureRatePercent is not null);
        RuleFor(x => x.Algorithm).Must(a => a is null || KassenSichV.Algorithms.Contains(a))
            .WithMessage($"Supported algorithms: {string.Join(", ", KassenSichV.Algorithms)}.");
        RuleFor(x => x.ClientId).MaximumLength(FiscalConfiguration.MaxClientIdLength);
        RuleFor(x => x.FiskalyEnvironment).Must(e => e is null or "Sandbox" or "Production").WithMessage("Sandbox or Production.");
        RuleFor(x => x.FiskalyCredentialReference).MaximumLength(FiscalConfiguration.MaxReferenceLength);
        RuleFor(x => x.FiskalyApiKey).MaximumLength(200);
        RuleFor(x => x.FiskalyApiSecret).MaximumLength(500);
        RuleFor(x => x).Must(x => (x.FiskalyApiKey is null) == (x.FiskalyApiSecret is null))
            .WithName("FiskalyApiSecret").WithMessage("Enter the fiskaly API key and secret together.");
        RuleFor(x => x).Must(x => !(x.ClearFiskalyCredentials && x.FiskalyApiKey is not null))
            .WithName("ClearFiskalyCredentials").WithMessage("Either clear the stored credentials or enter new ones.");
    }
}

/// <summary>
/// Platform administration of the fiscal layer across organizations. Every organization is processed in its own scope as
/// its system actor, so reads and writes stay under that organization's RLS policies (like the invoicing runner).
/// </summary>
internal sealed class FiscalAdministration(
    IServiceScopeFactory scopes,
    IOrganizationModuleDirectory modules,
    IOrganizationDirectory organizations,
    ICurrentUser currentUser,
    IHostEnvironment environment,
    IOptions<PosOptions> options,
    IClock clock)
{
    public async Task<IReadOnlyList<OrganizationFiscalSummaryDto>> ListAsync(string? search, CancellationToken ct)
    {
        var list = await modules.ListWithModuleAsync(PlatformModule.Pos, search, 200, ct);
        var result = new List<OrganizationFiscalSummaryDto>();
        foreach (var org in list)
        {
            await using var scope = ScopeFor(org.Id);
            var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
            var effective = await scope.ServiceProvider.GetRequiredService<FiscalSignerProvider>().EffectiveAsync(null, ct);
            var registers = await db.Set<Register>().CountAsync(r => r.OrganizationId == org.Id, ct);
            var outages = await db.Set<FiscalOutage>().CountAsync(o => o.OrganizationId == org.Id && o.EndedAt == null, ct);
            var serial = await db.Set<SimulatedTse>().Where(t => t.OrganizationId == org.Id).Select(t => t.SerialNumber).FirstOrDefaultAsync(ct);
            result.Add(new OrganizationFiscalSummaryDto(org.Id, org.Name, org.City, org.IsVerified, effective.Mode?.ToString(), effective.ModeSource,
                registers, outages, serial));
        }
        return result;
    }

    public async Task<Result<OrganizationFiscalDto>> GetAsync(Guid organizationId, CancellationToken ct)
    {
        var org = (await organizations.GetAsync([organizationId], ct)).GetValueOrDefault(organizationId);
        if (org is null)
            return Error.NotFound("organization.not_found", "Organization not found.");
        await using var scope = ScopeFor(organizationId);
        return await DetailAsync(scope.ServiceProvider, org, ct);
    }

    public async Task<Result<OrganizationFiscalDto>> SetOrganizationAsync(Guid organizationId, FiscalConfigRequest request, CancellationToken ct) =>
        await SaveAsync(organizationId, null, request, ct);

    public async Task<Result<OrganizationFiscalDto>> SetRegisterAsync(Guid organizationId, Guid registerId, FiscalConfigRequest request, CancellationToken ct) =>
        await SaveAsync(organizationId, registerId, request, ct);

    private async Task<Result<OrganizationFiscalDto>> SaveAsync(Guid organizationId, Guid? registerId, FiscalConfigRequest request, CancellationToken ct)
    {
        var org = (await organizations.GetAsync([organizationId], ct)).GetValueOrDefault(organizationId);
        if (org is null)
            return Error.NotFound("organization.not_found", "Organization not found.");
        if (request.Mode == FiscalMode.SimulatedTse && environment.IsProduction() && !options.Value.Fiscal.AllowSimulatedTseInProduction)
            return Error.Conflict("pos.fiscal_simulated_not_allowed", "The simulated test TSE must not be used in production.");
        if (registerId is not null && (request.FiskalyApiKey is not null || request.FiskalyCredentialReference is not null || request.FiskalyTssId is not null
                                       || request.Algorithm is not null))
            return Error.Validation("pos.fiscal_register_scope", "TSS, credentials and algorithm are set for the organization, not per register.");

        await using var scope = ScopeFor(organizationId);
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<HandyNetDbContext>();
        if (registerId is { } rid && !await db.Set<Register>().AnyAsync(r => r.Id == rid && r.OrganizationId == organizationId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var config = await db.Set<FiscalConfiguration>().FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.RegisterId == registerId, ct);
        var settings = new FiscalSettings(request.Mode, request.LatencyMs, request.FailureRatePercent, request.ForcedOutage, request.Algorithm,
            string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId, request.FiskalyTssId, request.FiskalyEnvironment,
            string.IsNullOrWhiteSpace(request.FiskalyCredentialReference) ? null : request.FiskalyCredentialReference.Trim());
        if (config is null)
        {
            var created = FiscalConfiguration.Create(organizationId, registerId, settings, currentUser.UserId, clock.UtcNow);
            if (!created.IsSuccess)
                return created.Error!;
            config = created.Value;
            db.Add(config);
        }
        else
        {
            var applied = config.Apply(settings, currentUser.UserId, clock.UtcNow);
            if (!applied.IsSuccess)
                return applied.Error!;
        }

        if (request.ClearFiskalyCredentials)
            config.SetProtectedCredentials(null, null);
        else if (request.FiskalyApiKey is { } key && request.FiskalyApiSecret is { } secret)
        {
            var protector = services.GetRequiredService<IFiscalSecretProtector>();
            config.SetProtectedCredentials(protector.Protect(key.Trim()), protector.Protect(secret.Trim()));
        }

        try
        {
            await db.SaveChangesAsync(ct);
            // The simulated TSE exists as soon as it is chosen, so its serial number and key can be shown right away.
            var mode = (await services.GetRequiredService<FiscalSignerProvider>().EffectiveAsync(registerId, ct)).Mode;
            if (mode == FiscalMode.SimulatedTse)
                await services.GetRequiredService<ISimulatedTseStore>().LockAsync(organizationId,
                    (registerId is null ? config.SimulatedAlgorithm : null) ?? KassenSichV.AlgorithmP256, ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("concurrency", "The configuration was changed concurrently. Reload and try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The configuration was changed concurrently. Reload and try again.");
        }

        db.ChangeTracker.Clear();
        return await DetailAsync(services, org, ct);
    }

    private AsyncServiceScope ScopeFor(Guid organizationId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(organizationId);
        return scope;
    }

    private async Task<OrganizationFiscalDto> DetailAsync(IServiceProvider services, OrganizationSummary org, CancellationToken ct)
    {
        var db = services.GetRequiredService<HandyNetDbContext>();
        var provider = services.GetRequiredService<FiscalSignerProvider>();
        var orgEffective = await provider.EffectiveAsync(null, ct);
        var registers = await db.Set<Register>().AsNoTracking().Where(r => r.OrganizationId == org.Id).OrderBy(r => r.Name).ToListAsync(ct);
        var openOutages = await db.Set<FiscalOutage>().AsNoTracking().Where(o => o.OrganizationId == org.Id && o.EndedAt == null)
            .Select(o => o.RegisterId).ToListAsync(ct);
        var registerDtos = new List<RegisterFiscalDto>();
        foreach (var register in registers)
        {
            var e = await provider.EffectiveAsync(register.Id, ct);
            registerDtos.Add(new RegisterFiscalDto(register.Id, register.Name, register.IsActive, e.Mode?.ToString(), e.ModeSource, e.ClientId,
                e.Simulated.ForcedOutage, e.Simulated.LatencyMs, e.Simulated.FailureRatePercent, openOutages.Contains(register.Id),
                e.Register is null ? null : ToDto(e.Register)));
        }

        var names = registers.ToDictionary(r => r.Id, r => r.Name);
        var outages = await db.Set<FiscalOutage>().AsNoTracking().Where(o => o.OrganizationId == org.Id)
            .OrderByDescending(o => o.EndedAt == null).ThenByDescending(o => o.StartedAt).Take(50).ToListAsync(ct);

        return new OrganizationFiscalDto(org.Id, org.Name, orgEffective.Mode?.ToString(), orgEffective.ModeSource, options.Value.FiscalSigner?.ToString(),
            orgEffective.Organization is null ? null : ToDto(orgEffective.Organization), registerDtos, await SimulatedTseAsync(db, org.Id, ct),
            outages.Select(o => new FiscalOutageDto(o.Id, o.RegisterId, names.GetValueOrDefault(o.RegisterId), o.SessionId, o.Mode.ToString(), o.StartedAt,
                o.EndedAt, o.Reason)).ToList(),
            environment.IsProduction(), !environment.IsProduction() || options.Value.Fiscal.AllowSimulatedTseInProduction, KassenSichV.Algorithms);
    }

    internal static async Task<SimulatedTseDto?> SimulatedTseAsync(HandyNetDbContext db, Guid organizationId, CancellationToken ct)
    {
        var tse = await db.Set<SimulatedTse>().AsNoTracking().FirstOrDefaultAsync(t => t.OrganizationId == organizationId, ct);
        if (tse is null)
            return null;
        var last = await db.Set<SimulatedTseTransaction>().AsNoTracking().Where(t => t.TseId == tse.Id).OrderByDescending(t => t.Number)
            .Select(t => new TseSignatureDto(t.Number, t.SignatureCounter ?? t.StartSignatureCounter, t.StartedAt, t.FinishedAt, t.Signature ?? t.StartSignature,
                t.ClientId, t.ProcessType))
            .FirstOrDefaultAsync(ct);
        return new SimulatedTseDto(tse.Id, tse.SerialNumber, tse.PublicKey, tse.SignatureAlgorithm, tse.TimeFormat, tse.TransactionCounter, tse.SignatureCounter,
            tse.CreatedAt, last);
    }

    internal static FiscalConfigDto ToDto(FiscalConfiguration c) => new(c.Mode?.ToString(), c.SimulatedLatencyMs, c.SimulatedFailureRatePercent,
        c.SimulatedOutage, c.SimulatedAlgorithm, c.ClientId, c.FiskalyTssId, c.FiskalyEnvironment, c.FiskalyCredentialReference,
        c.FiskalyApiKeyProtected is not null && c.FiskalyApiSecretProtected is not null, c.UpdatedAt);
}

/// <summary>TSE state of one register for the till's top bar (tenant scope, RLS).</summary>
internal sealed class FiscalStatusService(HandyNetDbContext db, ITenantContext tenant, FiscalSignerProvider provider)
{
    public async Task<Result<RegisterFiscalStatusDto>> GetAsync(Guid registerId, CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        if (!await db.Set<Register>().AnyAsync(r => r.Id == registerId && r.OrganizationId == organizationId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var effective = await provider.EffectiveAsync(registerId, ct);
        var outage = await db.Set<FiscalOutage>().AsNoTracking().Where(o => o.RegisterId == registerId && o.EndedAt == null).FirstOrDefaultAsync(ct);
        var last = await db.Set<Receipt>().AsNoTracking().Where(r => r.RegisterId == registerId && r.FiscalStatus == FiscalStatus.Signed)
            .OrderByDescending(r => r.Number).Select(r => new { r.FiscalFinishedAt, r.FiscalTseSerialNumber }).FirstOrDefaultAsync(ct);
        string? serial = last?.FiscalTseSerialNumber;
        if (effective.Mode == FiscalMode.SimulatedTse)
            serial = (await FiscalAdministration.SimulatedTseAsync(db, organizationId, ct))?.SerialNumber ?? serial;

        var refusal = provider.Gate(effective.Mode);
        var isTest = effective.Mode == FiscalMode.SimulatedTse
                     || (effective.Mode == FiscalMode.FiskalyCloudTse && effective.Organization?.FiskalyEnvironment != "Production");
        var state = effective.Mode switch
        {
            null => "NotConfigured",
            _ when refusal is not null => "Refused",
            FiscalMode.Disabled => "Disabled",
            _ when outage is not null || (effective.Mode == FiscalMode.SimulatedTse && effective.Simulated.ForcedOutage) => "Outage",
            _ when isTest => "Test",
            _ => "Ok",
        };
        return new RegisterFiscalStatusDto(registerId, effective.Mode?.ToString(), state, isTest, serial, effective.ClientId, outage?.StartedAt,
            outage?.Reason, refusal?.Code, last?.FiscalFinishedAt);
    }
}
