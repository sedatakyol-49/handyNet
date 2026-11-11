using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Create/update body of a card terminal profile. Version: required on update (optimistic concurrency).</summary>
public sealed record TerminalProfileRequest(
    string Name,
    TerminalConnection Connection,
    string? Host = null,
    int? Port = null,
    string? BridgeUrl = null,
    string? ZvtPassword = null,
    bool? IsDefault = null,
    uint? Version = null)
{
    public TerminalProfileSettings ToSettings() => new(Name, Connection, Host, Port, BridgeUrl, ZvtPassword);
}

public sealed record TerminalProfileDto(
    Guid Id,
    Guid RegisterId,
    string Name,
    TerminalConnection Connection,
    string? Host,
    int? Port,
    string? BridgeUrl,
    string? ZvtPassword,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

/// <summary>Starts a terminal attempt (status Pending). TerminalProfileId: the profile the till is about to use (optional).</summary>
public sealed record StartCardTransactionRequest(CardTransactionKind Kind, long AmountMinor, Guid? TerminalProfileId = null);

/// <summary>The terminal's final result: Approved, Declined, Aborted or Failed, with what the terminal reported.</summary>
public sealed record CardTransactionResultRequest(
    CardTransactionStatus Status,
    string? TerminalId = null,
    string? TraceNumber = null,
    string? TerminalReceiptNumber = null,
    string? CardName = null,
    string? MaskedPan = null,
    string? AuthCode = null,
    string? ResultCode = null,
    string? ResultText = null,
    string? CustomerReceiptText = null,
    string? MerchantReceiptText = null)
{
    public CardResult ToResult() => new(TerminalId, TraceNumber, TerminalReceiptNumber, CardName, MaskedPan, AuthCode, ResultCode, ResultText,
        CustomerReceiptText, MerchantReceiptText);
}

public sealed record CardReversalRequest(string? ResultText = null);

public sealed record CardTransactionDto(
    Guid Id,
    Guid RegisterId,
    Guid? TerminalProfileId,
    string? TerminalName,
    bool IsTest,
    string Kind,
    long AmountMinor,
    string Status,
    string? TerminalId,
    string? TraceNumber,
    string? TerminalReceiptNumber,
    string? CardName,
    string? MaskedPan,
    string? AuthCode,
    string? ResultCode,
    string? ResultText,
    string? CustomerReceiptText,
    string? MerchantReceiptText,
    Guid? ReceiptId,
    long? ReceiptNumber,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    string? CreatedByName,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LinkedAt,
    DateTimeOffset? ReversedAt,
    string? ReversalText);

internal sealed class TerminalProfileValidator : AbstractValidator<TerminalProfileRequest>
{
    public TerminalProfileValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(TerminalProfile.MaxNameLength);
        RuleFor(x => x.Connection).IsInEnum();
        RuleFor(x => x.Host).MaximumLength(PrinterProfile.MaxHostLength);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
        RuleFor(x => x.BridgeUrl).MaximumLength(PrinterProfile.MaxBridgeUrlLength);
        RuleFor(x => x.ZvtPassword).MaximumLength(6);
    }
}

internal sealed class StartCardTransactionValidator : AbstractValidator<StartCardTransactionRequest>
{
    public StartCardTransactionValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.AmountMinor).InclusiveBetween(1, PosLimits.MaxAmountMinor);
    }
}

internal sealed class CardTransactionResultValidator : AbstractValidator<CardTransactionResultRequest>
{
    public CardTransactionResultValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.TerminalId).MaximumLength(CardTransaction.MaxShortLength);
        RuleFor(x => x.TraceNumber).MaximumLength(CardTransaction.MaxShortLength);
        RuleFor(x => x.TerminalReceiptNumber).MaximumLength(CardTransaction.MaxShortLength);
        RuleFor(x => x.AuthCode).MaximumLength(CardTransaction.MaxShortLength);
        RuleFor(x => x.ResultCode).MaximumLength(CardTransaction.MaxShortLength);
        RuleFor(x => x.CardName).MaximumLength(CardTransaction.MaxCardNameLength);
        RuleFor(x => x.MaskedPan).MaximumLength(CardTransaction.MaxPanLength);
        RuleFor(x => x.ResultText).MaximumLength(CardTransaction.MaxResultTextLength);
        RuleFor(x => x.CustomerReceiptText).MaximumLength(CardTransaction.MaxReceiptTextLength);
        RuleFor(x => x.MerchantReceiptText).MaximumLength(CardTransaction.MaxReceiptTextLength);
    }
}

internal sealed class CardReversalValidator : AbstractValidator<CardReversalRequest>
{
    public CardReversalValidator() => RuleFor(x => x.ResultText).MaximumLength(CardTransaction.MaxResultTextLength);
}

/// <summary>
/// Card terminal profiles per register (ADR-0021), like printer profiles: read with <c>pos.sell</c>, written with
/// <c>pos.manage</c>. The server stores configuration only and never contacts a terminal or a bridge.
/// </summary>
internal sealed class TerminalProfileService(HandyNetDbContext db, ITenantContext tenant, IClock clock, IHostEnvironment environment,
    IOptions<PosOptions> options)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    private static readonly Error NotFound = Error.NotFound("pos.terminal_not_found", "Terminal profile not found.");

    public async Task<Result<IReadOnlyList<TerminalProfileDto>>> ListAsync(Guid registerId, CancellationToken ct)
    {
        if (!await RegisterExistsAsync(registerId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var profiles = await db.Set<TerminalProfile>().AsNoTracking()
            .Where(p => p.OrganizationId == OrganizationId && p.RegisterId == registerId)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
        return profiles.Select(ToDto).ToList();
    }

    public async Task<Result<TerminalProfileDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<TerminalProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct) is { } p
            ? ToDto(p)
            : NotFound;

    /// <summary>The simulated terminal is test only (see <see cref="TerminalProfile.CheckSimulatedAllowed"/>).</summary>
    public Error? CheckSimulated(TerminalConnection connection) =>
        TerminalProfile.CheckSimulatedAllowed(connection, environment.IsProduction(), options.Value.Terminal.AllowSimulatedInProduction);

    public async Task<Result<TerminalProfileDto>> CreateAsync(Guid registerId, TerminalProfileRequest request, CancellationToken ct)
    {
        if (!await RegisterExistsAsync(registerId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (CheckSimulated(request.Connection) is { } notAllowed)
            return notAllowed;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.Set<TerminalProfile>().Where(p => p.OrganizationId == OrganizationId && p.RegisterId == registerId).ToListAsync(ct);
        if (existing.Count >= TerminalProfile.MaxProfilesPerRegister)
            return Error.Conflict("pos.terminal_limit", $"A register can have at most {TerminalProfile.MaxProfilesPerRegister} terminal profiles.");
        // The first profile of a register becomes its default.
        var makeDefault = existing.Count == 0 || request.IsDefault == true;
        var profile = TerminalProfile.Create(OrganizationId, registerId, request.ToSettings(), makeDefault, clock.UtcNow);
        if (!profile.IsSuccess)
            return profile.Error!;
        if (makeDefault)
            await ClearDefaultAsync(existing, ct);
        db.Add(profile.Value);
        var saved = await SaveAsync(ct);
        if (!saved.IsSuccess)
            return saved.Error!;
        await tx.CommitAsync(ct);
        return ToDto(profile.Value);
    }

    public async Task<Result<TerminalProfileDto>> UpdateAsync(Guid id, TerminalProfileRequest request, CancellationToken ct)
    {
        if (request.Version is not { } expected)
            return Error.Validation("pos.terminal_version", "The version of the profile is required.");
        if (CheckSimulated(request.Connection) is { } notAllowed)
            return notAllowed;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<TerminalProfile>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (profile is null)
            return NotFound;
        db.Entry(profile).Property(p => p.Version).OriginalValue = expected;
        var updated = profile.Update(request.ToSettings(), clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        if (request.IsDefault == true && !profile.IsDefault)
        {
            await ClearDefaultAsync(await OthersAsync(profile, ct), ct);
            profile.SetDefault(true);
        }
        var saved = await SaveAsync(ct);
        if (!saved.IsSuccess)
            return saved.Error!;
        await tx.CommitAsync(ct);
        return ToDto(profile);
    }

    /// <summary>Makes the profile the register's default (the previous default loses the flag in the same transaction).</summary>
    public async Task<Result<TerminalProfileDto>> SetDefaultAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<TerminalProfile>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (profile is null)
            return NotFound;
        if (!profile.IsDefault)
        {
            await ClearDefaultAsync(await OthersAsync(profile, ct), ct);
            profile.SetDefault(true);
            var saved = await SaveAsync(ct);
            if (!saved.IsSuccess)
                return saved.Error!;
        }
        await tx.CommitAsync(ct);
        return ToDto(profile);
    }

    /// <summary>Deletes the profile; when it was the default, the remaining profile with the first name becomes the default.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<TerminalProfile>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (profile is null)
            return NotFound;
        var others = await OthersAsync(profile, ct);
        db.Remove(profile);
        var saved = await SaveAsync(ct);
        if (!saved.IsSuccess)
            return saved.Error!;
        if (profile.IsDefault && others.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault() is { } next)
        {
            next.SetDefault(true);
            saved = await SaveAsync(ct);
            if (!saved.IsSuccess)
                return saved.Error!;
        }
        await tx.CommitAsync(ct);
        return Result.Success();
    }

    private Task<bool> RegisterExistsAsync(Guid registerId, CancellationToken ct) =>
        db.Set<Register>().AnyAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);

    private Task<List<TerminalProfile>> OthersAsync(TerminalProfile profile, CancellationToken ct) =>
        db.Set<TerminalProfile>().Where(p => p.OrganizationId == OrganizationId && p.RegisterId == profile.RegisterId && p.Id != profile.Id).ToListAsync(ct);

    /// <summary>Clears the old default first (own statement), so the partial unique index never sees two defaults.</summary>
    private async Task ClearDefaultAsync(IEnumerable<TerminalProfile> profiles, CancellationToken ct)
    {
        var current = profiles.Where(p => p.IsDefault).ToList();
        if (current.Count == 0)
            return;
        foreach (var p in current)
            p.SetDefault(false);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Result> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_terminal_profiles_one_default"))
        {
            return Error.Conflict("concurrency", "The terminal profiles were changed concurrently. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("pos.terminal_name_taken", "This register already has a terminal profile with this name.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The terminal profile was changed concurrently. Reload and try again.");
        }
    }

    internal static TerminalProfileDto ToDto(TerminalProfile p) =>
        new(p.Id, p.RegisterId, p.Name, p.Connection, p.Host, p.Port, p.BridgeUrl, p.ZvtPassword, p.IsDefault, p.CreatedAt, p.UpdatedAt, p.Version);
}

/// <summary>
/// Card transactions (ADR-0021): the till records every terminal attempt before it starts the terminal and reports the
/// result afterwards; a sale or return then names the approved transaction, which is linked in the receipt's transaction.
/// </summary>
internal sealed class CardTransactionService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IUserAccounts users, IClock clock,
    TerminalProfileService terminals)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    private static readonly Error NotFound = Error.NotFound("pos.card_transaction_not_found", "Card transaction not found.");

    public async Task<Result<CardTransactionDto>> StartAsync(Guid registerId, StartCardTransactionRequest request, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (actor.CheckRegister(registerId) is { } otherRegister)
            return otherRegister;
        if (!register.IsActive)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        // A refund on the terminal pays money back to the customer: the same right as a return.
        if (request.Kind == CardTransactionKind.Refund && !actor.Has(Permissions.PosRefund))
            return Error.Forbidden("permission.denied", $"Missing permission '{Permissions.PosRefund}'.");

        TerminalProfile? profile = null;
        if (request.TerminalProfileId is { } profileId)
        {
            profile = await db.Set<TerminalProfile>().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.OrganizationId == OrganizationId && p.RegisterId == registerId, ct);
            if (profile is null)
                return Error.NotFound("pos.terminal_not_found", "Terminal profile not found.");
            // A profile created before the environment became Production must not approve payments there either.
            if (terminals.CheckSimulated(profile.Connection) is { } notAllowed)
                return notAllowed;
        }

        var transaction = CardTransaction.Start(OrganizationId, registerId, request.Kind, request.AmountMinor, profile, actor.UserId, clock.UtcNow);
        if (!transaction.IsSuccess)
            return transaction.Error!;
        db.Add(transaction.Value);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(transaction.Value, ct);
    }

    public async Task<Result<CardTransactionDto>> CompleteAsync(Guid id, CardTransactionResultRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transaction = await LockAsync(db, id, ct);
        if (transaction is null || transaction.OrganizationId != OrganizationId)
            return NotFound;
        if (actor.CheckRegister(transaction.RegisterId) is { } otherRegister)
            return otherRegister;
        var completed = transaction.Complete(request.Status, request.ToResult(), clock.UtcNow);
        if (!completed.IsSuccess)
            return completed.Error!;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(transaction, ct);
    }

    public async Task<Result<CardTransactionDto>> ReverseAsync(Guid id, CardReversalRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transaction = await LockAsync(db, id, ct);
        if (transaction is null || transaction.OrganizationId != OrganizationId)
            return NotFound;
        if (actor.CheckRegister(transaction.RegisterId) is { } otherRegister)
            return otherRegister;
        var reversed = transaction.Reverse(request.ResultText, clock.UtcNow);
        if (!reversed.IsSuccess)
            return reversed.Error!;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(transaction, ct);
    }

    public async Task<Result<CardTransactionDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<CardTransaction>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.OrganizationId == OrganizationId, ct) is { } t
            ? await ToDtoAsync(t, ct)
            : NotFound;

    /// <summary>Newest first. unlinked = true: transactions no receipt uses (with status Approved: "card payments without a receipt").</summary>
    public async Task<Result<PagedResult<CardTransactionDto>>> ListAsync(Guid? registerId, string? status, bool? unlinked, int? page, int? pageSize,
        CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<CardTransaction>().AsNoTracking().Where(t => t.OrganizationId == OrganizationId);
        if (registerId is { } register)
            query = query.Where(t => t.RegisterId == register);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<CardTransactionStatus>(status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Error.Validation("pos.card_transaction_status", $"Unknown status '{status}'.");
            query = query.Where(t => t.Status == parsed);
        }
        if (unlinked is { } only)
            query = only ? query.Where(t => t.ReceiptId == null) : query.Where(t => t.ReceiptId != null);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var people = await users.GetAsync(rows.Select(t => t.CreatedBy).Distinct().ToList(), ct);
        var receiptIds = rows.Where(t => t.ReceiptId != null).Select(t => t.ReceiptId!.Value).ToList();
        var numbers = await db.Set<Receipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        return new PagedResult<CardTransactionDto>(
            rows.Select(t => ToDto(t, people.GetValueOrDefault(t.CreatedBy)?.DisplayName, t.ReceiptId is { } r && numbers.TryGetValue(r, out var n) ? n : null)).ToList(),
            p, size, total);
    }

    /// <summary>Loads the transaction under a row lock (tracked), so concurrent results, reversals and sales using it queue.</summary>
    internal static async Task<CardTransaction?> LockAsync(HandyNetDbContext db, Guid id, CancellationToken ct) =>
        (await db.Set<CardTransaction>().FromSql($"SELECT *, xmin FROM pos.card_transactions WHERE id = {id} FOR UPDATE").ToListAsync(ct))
        .FirstOrDefault();

    private async Task<CardTransactionDto> ToDtoAsync(CardTransaction t, CancellationToken ct)
    {
        var name = (await users.GetAsync([t.CreatedBy], ct)).GetValueOrDefault(t.CreatedBy)?.DisplayName;
        long? number = t.ReceiptId is { } receiptId
            ? await db.Set<Receipt>().AsNoTracking().Where(r => r.Id == receiptId).Select(r => (long?)r.Number).FirstOrDefaultAsync(ct)
            : null;
        return ToDto(t, name, number);
    }

    private static CardTransactionDto ToDto(CardTransaction t, string? createdByName, long? receiptNumber) =>
        new(t.Id, t.RegisterId, t.TerminalProfileId, t.TerminalName, t.IsTest, t.Kind.ToString(), t.AmountMinor, t.Status.ToString(), t.TerminalId,
            t.TraceNumber, t.TerminalReceiptNumber, t.CardName, t.MaskedPan, t.AuthCode, t.ResultCode, t.ResultText, t.CustomerReceiptText,
            t.MerchantReceiptText, t.ReceiptId, receiptNumber, t.CreatedAt, t.CreatedBy, createdByName, t.CompletedAt, t.LinkedAt, t.ReversedAt, t.ReversalText);
}

internal static class CardTerminalEndpoints
{
    public static void Map(RouteGroupBuilder sell, RouteGroupBuilder manage)
    {
        sell.MapGet("/registers/{id:guid}/terminals", async (Guid id, TerminalProfileService service, CancellationToken ct) =>
            (await service.ListAsync(id, ct)).ToHttp(Results.Ok));

        sell.MapGet("/terminals/{id:guid}", async (Guid id, TerminalProfileService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapPost("/registers/{id:guid}/terminals", async (Guid id, TerminalProfileRequest request, IValidator<TerminalProfileRequest> validator,
                TerminalProfileService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.CreateAsync(id, request, ct)).ToHttp(p => Results.Created($"/api/pos/terminals/{p.Id}", p)));

        manage.MapPut("/terminals/{id:guid}", async (Guid id, TerminalProfileRequest request, IValidator<TerminalProfileRequest> validator,
                TerminalProfileService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok));

        manage.MapPost("/terminals/{id:guid}/default", async (Guid id, TerminalProfileService service, CancellationToken ct) =>
            (await service.SetDefaultAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapDelete("/terminals/{id:guid}", async (Guid id, TerminalProfileService service, CancellationToken ct) =>
            (await service.DeleteAsync(id, ct)).ToHttp(Results.NoContent));

        sell.MapPost("/registers/{id:guid}/card-transactions", async (Guid id, StartCardTransactionRequest request,
                IValidator<StartCardTransactionRequest> validator, CardTransactionService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.StartAsync(id, request, ct)).ToHttp(t => Results.Created($"/api/pos/card-transactions/{t.Id}", t)));

        sell.MapPost("/card-transactions/{id:guid}/result", async (Guid id, CardTransactionResultRequest request,
                IValidator<CardTransactionResultRequest> validator, CardTransactionService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.CompleteAsync(id, request, ct)).ToHttp(Results.Ok));

        sell.MapPost("/card-transactions/{id:guid}/reversal", async (Guid id, CardReversalRequest request, IValidator<CardReversalRequest> validator,
                CardTransactionService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ReverseAsync(id, request, ct)).ToHttp(Results.Ok));

        sell.MapGet("/card-transactions", async (Guid? registerId, string? status, bool? unlinked, int? page, int? pageSize,
                CardTransactionService service, CancellationToken ct) =>
            (await service.ListAsync(registerId, status, unlinked, page, pageSize, ct)).ToHttp(Results.Ok));

        sell.MapGet("/card-transactions/{id:guid}", async (Guid id, CardTransactionService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));
    }
}
