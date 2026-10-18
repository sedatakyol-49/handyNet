using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

public sealed record SetOwnPinRequest(string? Pin, string CurrentPassword);

public sealed record SetPinRequest(string? Pin);

public sealed record CashierSignInRequest(Guid UserId, string Pin);

public sealed record PinStatusDto(Guid UserId, bool HasPin, DateTimeOffset? LockedUntil, DateTimeOffset? UpdatedAt);

/// <summary>A member who can be switched to at the till (has a PIN).</summary>
public sealed record CashierOptionDto(Guid UserId, string DisplayName, string Role, bool Locked);

/// <summary>The cashier the till acts as: identity, effective POS rights and the session limits.</summary>
public sealed record CashierDto(
    Guid SessionId,
    Guid RegisterId,
    Guid UserId,
    string? DisplayName,
    string Role,
    IReadOnlyList<string> Permissions,
    int MaxDiscountPercent,
    DateTimeOffset ExpiresAt,
    int? AutoLockMinutes);

/// <summary>Returned once at sign-in: the bearer value for <see cref="PosActor.CashierHeader"/> (stored only as a hash).</summary>
public sealed record CashierSignInDto(string Token, CashierDto Cashier);

internal sealed class SetOwnPinValidator : AbstractValidator<SetOwnPinRequest>
{
    public SetOwnPinValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Pin).MaximumLength(PinPolicy.MaxLength);
    }
}

internal sealed class SetPinValidator : AbstractValidator<SetPinRequest>
{
    public SetPinValidator() => RuleFor(x => x.Pin).MaximumLength(PinPolicy.MaxLength);
}

internal sealed class CashierSignInValidator : AbstractValidator<CashierSignInRequest>
{
    public CashierSignInValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Pin).NotEmpty().MaximumLength(PinPolicy.MaxLength);
    }
}

/// <summary>A cashier resolved from a valid cashier token for this request.</summary>
internal sealed record ActingCashier(Guid SessionId, Guid RegisterId, Guid UserId, MembershipRole Role, IReadOnlySet<string> Permissions,
    int MaxDiscountPercent, DateTimeOffset ExpiresAt);

/// <summary>
/// Who the till acts as in this request. Without a cashier token: the signed-in user with the tenant's permissions. With a
/// valid <see cref="CashierHeader"/>: the cashier who entered their PIN on this shared device, with that member's rights and
/// bound to one register. Every POS service records <see cref="UserId"/> and checks rights through this actor.
/// </summary>
internal sealed class PosActor(ITenantContext tenant, ICurrentUser currentUser)
{
    public const string CashierHeader = "X-Pos-Cashier";

    public ActingCashier? Cashier { get; private set; }

    public Guid UserId => Cashier?.UserId ?? currentUser.UserId;

    public IReadOnlySet<string> Permissions => Cashier?.Permissions ?? tenant.Permissions;

    public int MaxDiscountPercent => Cashier?.MaxDiscountPercent ?? tenant.MaxDiscountPercent;

    public bool Has(string permission) => Permissions.Contains(permission);

    public void ActAs(ActingCashier cashier) => Cashier = cashier;

    /// <summary>A cashier session is bound to its register: writes on another register are refused.</summary>
    public Error? CheckRegister(Guid registerId) =>
        Cashier is { } c && c.RegisterId != registerId
            ? Error.Forbidden("pos.cashier_register_mismatch", "The cashier signed in on another register. Sign in on this register first.")
            : null;
}

/// <summary>Endpoints that read the cashier header themselves (sign-in with a stale token, lock).</summary>
internal sealed class IgnoreCashierTokenMetadata;

internal static class PosActorEndpoints
{
    /// <summary>Resolves <see cref="PosActor.CashierHeader"/> once per request (invalid/expired token: 403 pos.cashier_invalid).</summary>
    public static TBuilder ResolvePosActor<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var token = http.Request.Headers[PosActor.CashierHeader].ToString();
            if (string.IsNullOrWhiteSpace(token) || http.GetEndpoint()?.Metadata.GetMetadata<IgnoreCashierTokenMetadata>() is not null
                || http.RequestServices.GetRequiredService<ITenantContext>().OrganizationId is null)
                return await next(context);
            var cashiers = http.RequestServices.GetRequiredService<CashierService>();
            var resolved = await cashiers.ResolveAsync(token.Trim(), http.RequestAborted);
            if (!resolved.IsSuccess)
                return Problems.From(resolved.Error!);
            http.RequestServices.GetRequiredService<PosActor>().ActAs(resolved.Value);
            return await next(context);
        });
        return builder;
    }

    /// <summary>Like <c>RequirePermission</c>, but checks the acting cashier's rights (or the user's without a cashier).</summary>
    public static TBuilder RequirePosPermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireTenant();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            if (services.GetRequiredService<ITenantContext>().OrganizationId is not null && !services.GetRequiredService<PosActor>().Has(permission))
                return Problems.From(Error.Forbidden("permission.denied", $"Missing permission '{permission}'."));
            return await next(context);
        });
        return builder;
    }
}

/// <summary>
/// POS PINs and cashier switching on a shared device (Odoo-style): members set a PIN (own: with their password; owner: on
/// the Team page), a cashier signs in on a register with their PIN and the till then acts as that member until it is
/// locked, the session expires or (register auto-lock) after inactivity.
/// </summary>
internal sealed class CashierService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IOrganizationDirectory organizations,
    IUserAccounts users,
    IOptions<PosOptions> options,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    // ---- PINs -----------------------------------------------------------------------------------------------------------

    public async Task<PinStatusDto> GetOwnPinAsync(CancellationToken ct)
    {
        var pin = await db.Set<CashierPin>().AsNoTracking().FirstOrDefaultAsync(p => p.OrganizationId == OrganizationId && p.UserId == currentUser.UserId, ct);
        return ToStatus(currentUser.UserId, pin);
    }

    /// <summary>The signed-in member sets (or with null clears) their own PIN; needs the account password.</summary>
    public async Task<Result<PinStatusDto>> SetOwnPinAsync(SetOwnPinRequest request, CancellationToken ct)
    {
        if (!await users.VerifyPasswordAsync(currentUser.UserId, request.CurrentPassword, ct))
            return Error.Forbidden("pos.password_invalid", "The password is not correct.");
        return await SetAsync(currentUser.UserId, request.Pin, ct);
    }

    /// <summary>The owner sets or clears a member's PIN (also lifts a lockout).</summary>
    public async Task<Result<PinStatusDto>> SetPinForAsync(Guid userId, SetPinRequest request, CancellationToken ct)
    {
        if (await organizations.GetMemberAccessAsync(OrganizationId, userId, ct) is null)
            return Error.NotFound("staff.not_found", "Staff member not found.");
        return await SetAsync(userId, request.Pin, ct);
    }

    public async Task<IReadOnlyList<PinStatusDto>> ListPinsAsync(CancellationToken ct)
    {
        var members = await organizations.GetMembersAccessAsync(OrganizationId, ct);
        var pins = await db.Set<CashierPin>().AsNoTracking().Where(p => p.OrganizationId == OrganizationId).ToDictionaryAsync(p => p.UserId, ct);
        return members.Select(m => ToStatus(m.UserId, pins.GetValueOrDefault(m.UserId))).ToList();
    }

    /// <summary>Members who can be switched to at the till: POS sellers with a PIN.</summary>
    public async Task<IReadOnlyList<CashierOptionDto>> ListCashiersAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var pins = await db.Set<CashierPin>().AsNoTracking().Where(p => p.OrganizationId == OrganizationId && p.PinHash != null).ToListAsync(ct);
        var members = (await organizations.GetMembersAccessAsync(OrganizationId, ct)).Where(m => m.Permissions.Contains(Permissions.PosSell))
            .ToDictionary(m => m.UserId);
        var names = await users.GetAsync(pins.Select(p => p.UserId).ToList(), ct);
        return pins.Where(p => members.ContainsKey(p.UserId) && names.ContainsKey(p.UserId))
            .Select(p => new CashierOptionDto(p.UserId, names[p.UserId].DisplayName, members[p.UserId].Role.ToString(), p.IsLocked(now)))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<Result<PinStatusDto>> SetAsync(Guid userId, string? pin, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await db.Set<CashierPin>().FirstOrDefaultAsync(p => p.OrganizationId == OrganizationId && p.UserId == userId, ct);
        var isNew = row is null;
        row ??= CashierPin.For(OrganizationId, userId, now, currentUser.UserId);
        var set = row.Set(string.IsNullOrEmpty(pin) ? null : pin, currentUser.UserId, now);
        if (!set.IsSuccess)
            return set.Error!;
        if (isNew)
            db.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException u && u.IsUniqueViolation()))
        {
            return Error.Conflict("concurrency", "The PIN was changed concurrently. Try again.");
        }
        return ToStatus(userId, row);
    }

    private static PinStatusDto ToStatus(Guid userId, CashierPin? pin) =>
        new(userId, pin?.HasPin == true, pin?.HasPin == true ? pin.LockedUntil : null, pin?.UpdatedAt);

    // ---- PIN verification (cashier sign-in and manager approval) --------------------------------------------------------

    /// <summary>
    /// Checks <paramref name="pin"/> of a member inside the caller's transaction: the PIN row is locked (parallel guesses
    /// queue up), the attempt is recorded and saved even when it fails, so the caller must commit in either case.
    /// </summary>
    public async Task<Result> VerifyPinAsync(Guid userId, string pin, CancellationToken ct)
    {
        var rows = await db.Set<CashierPin>()
            .FromSql($"SELECT *, xmin FROM pos.cashier_pins WHERE organization_id = {OrganizationId} AND user_id = {userId} FOR UPDATE")
            .ToListAsync(ct);
        var row = rows.FirstOrDefault();
        var now = clock.UtcNow;
        var check = row?.Verify(pin, now) ?? PinCheck.NotSet;
        if (row is not null)
            await db.SaveChangesAsync(ct);
        return check switch
        {
            PinCheck.Valid => Result.Success(),
            PinCheck.NotSet => Error.Conflict("pos.pin_not_set", "This member has no POS PIN."),
            PinCheck.Locked => Error.Forbidden("pos.pin_locked",
                    $"Too many wrong PINs. Try again after {row!.LockedUntil:HH:mm} UTC or ask the owner to set a new PIN.")
                .WithDetails(new Dictionary<string, object?> { ["lockedUntil"] = row.LockedUntil }),
            _ => Error.Forbidden("pos.pin_invalid", "The PIN is not correct.")
                .WithDetails(new Dictionary<string, object?> { ["remainingAttempts"] = row!.RemainingAttempts }),
        };
    }

    // ---- cashier sessions -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A member signs in on a register of this device's login with their PIN. Earlier cashier sessions of the same device
    /// login on this register end. Wrong PINs count towards the lockout even though the request fails.
    /// </summary>
    public async Task<Result<CashierSignInDto>> SignInAsync(Guid registerId, CashierSignInRequest request, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (!register.IsActive)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        var member = await organizations.GetMemberAccessAsync(OrganizationId, request.UserId, ct);
        if (member is null || !member.Permissions.Contains(Permissions.PosSell))
            return Error.NotFound("pos.cashier_not_found", "No POS cashier with this id in the organization.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var verified = await VerifyPinAsync(request.UserId, request.Pin, ct);
        if (!verified.IsSuccess)
        {
            await tx.CommitAsync(ct); // keep the failed attempt
            return verified.Error!;
        }

        var now = clock.UtcNow;
        var previous = await db.Set<CashierSession>()
            .Where(s => s.OrganizationId == OrganizationId && s.RegisterId == registerId && s.DeviceUserId == currentUser.UserId && s.EndedAt == null)
            .ToListAsync(ct);
        foreach (var session in previous)
            session.End(CashierSessionEnd.Replaced, now);
        var token = OpaqueToken.New();
        var started = CashierSession.Start(OrganizationId, registerId, request.UserId, currentUser.UserId, OpaqueToken.HashOf(token), now,
            TimeSpan.FromHours(options.Value.CashierSessionHours));
        db.Add(started);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var cashier = new ActingCashier(started.Id, registerId, member.UserId, member.Role, member.Permissions, member.MaxDiscountPercent, started.ExpiresAt);
        return new CashierSignInDto(token, await ToDtoAsync(cashier, register.AutoLockMinutes, ct));
    }

    /// <summary>
    /// Validates a cashier token for this request: same organization (RLS), same device login, not ended, not expired, not
    /// idle beyond the register's auto-lock, and the cashier is still a member with <c>pos.sell</c>. The rights are read
    /// fresh on every request.
    /// </summary>
    public async Task<Result<ActingCashier>> ResolveAsync(string token, CancellationToken ct)
    {
        var invalid = Error.Forbidden("pos.cashier_invalid", "The cashier sign-in has ended (locked, expired or idle). Enter the PIN again.");
        if (!OpaqueToken.LooksValid(token))
            return invalid;
        var hash = OpaqueToken.HashOf(token);
        var found = await (from s in db.Set<CashierSession>().AsNoTracking()
                           join r in db.Set<Register>().AsNoTracking() on s.RegisterId equals r.Id
                           where s.TokenHash == hash && s.OrganizationId == OrganizationId
                           select new { Session = s, r.AutoLockMinutes }).FirstOrDefaultAsync(ct);
        var now = clock.UtcNow;
        if (found is null || found.Session.DeviceUserId != currentUser.UserId || !found.Session.IsActive(now, found.AutoLockMinutes))
            return invalid;
        var member = await organizations.GetMemberAccessAsync(OrganizationId, found.Session.CashierUserId, ct);
        if (member is null || !member.Permissions.Contains(Permissions.PosSell))
            return invalid;
        if (found.Session.NeedsTouch(now))
            await db.Set<CashierSession>().Where(s => s.Id == found.Session.Id && s.EndedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now), ct);
        return new ActingCashier(found.Session.Id, found.Session.RegisterId, member.UserId, member.Role, member.Permissions, member.MaxDiscountPercent,
            found.Session.ExpiresAt);
    }

    public async Task<Result<CashierDto>> CurrentAsync(PosActor actor, CancellationToken ct)
    {
        if (actor.Cashier is not { } cashier)
            return Error.NotFound("pos.cashier_none", "No cashier is signed in on this device.");
        var autoLock = await db.Set<Register>().AsNoTracking().Where(r => r.Id == cashier.RegisterId).Select(r => r.AutoLockMinutes).FirstOrDefaultAsync(ct);
        return await ToDtoAsync(cashier, autoLock, ct);
    }

    /// <summary>Locks the till: ends the cashier session of the presented token (idempotent; unknown tokens are ignored).</summary>
    public async Task LockAsync(string? token, CancellationToken ct)
    {
        if (!OpaqueToken.LooksValid(token))
            return;
        var hash = OpaqueToken.HashOf(token!);
        var session = await db.Set<CashierSession>()
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.OrganizationId == OrganizationId && s.DeviceUserId == currentUser.UserId, ct);
        if (session is null || session.EndedAt is not null)
            return;
        session.End(CashierSessionEnd.Locked, clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    private async Task<CashierDto> ToDtoAsync(ActingCashier c, int? autoLockMinutes, CancellationToken ct)
    {
        var name = (await users.GetAsync([c.UserId], ct)).GetValueOrDefault(c.UserId)?.DisplayName;
        return new CashierDto(c.SessionId, c.RegisterId, c.UserId, name, c.Role.ToString(),
            c.Permissions.Where(p => p.StartsWith("pos.", StringComparison.Ordinal)).Order().ToList(), c.MaxDiscountPercent, c.ExpiresAt, autoLockMinutes);
    }
}
