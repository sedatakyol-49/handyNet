using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Identity.Application;

/// <summary>Filters of the platform-admin user list. Search matches email and display name (case-insensitive, contains).</summary>
public sealed record AdminUserQuery(
    string? Search,
    bool? EmailVerified,
    bool? Locked,
    bool? Disabled,
    bool? PlatformAdmin,
    bool? NoOrganization,
    int? Page,
    int? PageSize);

/// <param name="IsLockedOut">Temporarily locked after too many failed sign-ins (until <see cref="LockedUntil"/>).</param>
/// <param name="IsDisabled">Disabled by a platform admin: no sign-in, no refresh, access tokens rejected.</param>
public sealed record AdminUserListItem(
    Guid Id,
    string Email,
    string DisplayName,
    bool EmailVerified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool IsLockedOut,
    DateTimeOffset? LockedUntil,
    bool IsDisabled,
    bool IsPlatformAdmin,
    IReadOnlyList<UserMembershipInfo> Memberships);

/// <param name="ActiveSessions">Signed-in devices: refresh-token families with a live token.</param>
/// <param name="IsSelf">The account of the calling admin (self-disable and self-revoke are refused).</param>
public sealed record AdminUserDetail(
    Guid Id,
    string Email,
    string DisplayName,
    bool EmailVerified,
    DateTimeOffset? EmailVerifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    int FailedLoginCount,
    DateTimeOffset? LockedUntil,
    bool IsLockedOut,
    bool IsDisabled,
    DateTimeOffset? DisabledAt,
    string? DisabledReason,
    bool IsPlatformAdmin,
    int ActiveSessions,
    DateTimeOffset? VerificationEmailRequestedAt,
    DateTimeOffset? PasswordResetRequestedAt,
    IReadOnlyList<UserMembershipInfo> Memberships,
    bool IsSelf);

/// <summary>Reason (required for destructive actions, optional otherwise); stored in the audit log.</summary>
public sealed record AdminReasonRequest(string? Reason);

/// <param name="Language">Language of the email ("de" default, "en").</param>
public sealed record AdminEmailRequest(string? Reason, string? Language);

public sealed record AdminDisplayNameRequest(string DisplayName, string? Reason);

internal sealed class AdminUserQueryValidator : AbstractValidator<AdminUserQuery>
{
    public AdminUserQueryValidator() => RuleFor(x => x.Search).MaximumLength(200);
}

internal sealed class AdminReasonRequestValidator : AbstractValidator<AdminReasonRequest>
{
    public AdminReasonRequestValidator() => RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
}

internal sealed class AdminEmailRequestValidator : AbstractValidator<AdminEmailRequest>
{
    public AdminEmailRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
        RuleFor(x => x.Language).Must(EmailLanguage.IsSupported).WithMessage("Language must be one of de, en, tr, es, ru, ar, fa.");
    }
}

internal sealed class AdminDisplayNameRequestValidator : AbstractValidator<AdminDisplayNameRequest>
{
    public AdminDisplayNameRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(User.MaxDisplayNameLength);
        RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
    }
}

/// <summary>
/// Platform administration of user accounts (contract/account data only — never passwords, hashes or tokens). Every write
/// is audited with the acting admin and reason (<see cref="AdminAudit"/>); opening a user's detail is access-logged.
/// Admin calls run without a tenant (no <c>X-Organization-Id</c>).
/// </summary>
internal sealed class UserAdministrationService(
    HandyNetDbContext db,
    IUserMembershipSource memberships,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IClock clock,
    ILogger<UserAdministrationService> logger)
{
    public const string LastAdminConstraint = "ck_users_last_platform_admin";

    private static readonly Error NotFound = Error.NotFound("user.not_found", "User not found.");

    public async Task<Result<PagedResult<AdminUserListItem>>> ListAsync(AdminUserQuery q, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var now = clock.UtcNow;
        var query = db.Set<User>().AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = Text.LikePattern(q.Search);
            query = query.Where(u => EF.Functions.ILike(u.Email, pattern) || EF.Functions.ILike(u.DisplayName, pattern));
        }
        if (q.EmailVerified is { } verified)
            query = verified ? query.Where(u => u.EmailVerifiedAt != null) : query.Where(u => u.EmailVerifiedAt == null);
        if (q.Locked is { } locked)
            query = locked ? query.Where(u => u.LockedUntil != null && u.LockedUntil > now) : query.Where(u => u.LockedUntil == null || u.LockedUntil <= now);
        if (q.Disabled is { } disabled)
            query = query.Where(u => u.IsActive != disabled);
        if (q.PlatformAdmin is { } admin)
            query = query.Where(u => u.IsPlatformAdmin == admin);
        if (q.NoOrganization is { } none)
        {
            var members = memberships.UserIdsWithMembership();
            query = none ? query.Where(u => !members.Contains(u.Id)) : query.Where(u => members.Contains(u.Id));
        }

        var total = await query.CountAsync(ct);
        var users = await query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var byUser = await memberships.GetAsync(users.Select(u => u.Id).ToList(), ct);

        var items = users.Select(u => new AdminUserListItem(u.Id, u.Email, u.DisplayName, u.EmailVerified, u.CreatedAt, u.LastLoginAt,
                u.IsLockedOut(now), u.LockedUntil, !u.IsActive, u.IsPlatformAdmin, byUser.GetValueOrDefault(u.Id) ?? []))
            .ToList();
        return new PagedResult<AdminUserListItem>(items, page, size, total);
    }

    /// <summary>Detail view; writes an access-log entry (the admin opened this account).</summary>
    public async Task<Result<AdminUserDetail>> GetAsync(Guid id, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound;
        AdminAudit.Access(db, clock, currentUser.UserId, nameof(User), id.ToString(), null, "user.detail");
        await db.SaveChangesAsync(ct);
        return await ToDetailAsync(user, ct);
    }

    /// <summary>Disables the account and ends all its sessions in one transaction.</summary>
    public Task<Result<AdminUserDetail>> DisableAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "Disable", reason, async (user, admin, now) =>
        {
            var disabled = user.Disable(admin, reason, now);
            if (!disabled.IsSuccess)
                return disabled;
            if (user.IsPlatformAdmin && await IsLastActivePlatformAdminAsync(user.Id, ct))
                return LastAdmin();
            await SessionRevocation.StageRevokeAllAsync(db, user.Id, RefreshTokenRevocation.AccountDisabled, now, ct);
            return Result.Success();
        }, ct);

    public Task<Result<AdminUserDetail>> EnableAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "Enable", reason, (user, _, _) => Task.FromResult(user.Enable()), ct);

    public Task<Result<AdminUserDetail>> ClearLockoutAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "ClearLockout", reason, (user, _, now) => Task.FromResult(user.ClearLockout(now)), ct);

    /// <summary>Ends every session (all devices). Access tokens already issued stay valid until they expire (max. 15 minutes).</summary>
    public Task<Result<AdminUserDetail>> RevokeSessionsAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "RevokeSessions", reason, async (user, _, now) =>
        {
            await SessionRevocation.StageRevokeAllAsync(db, user.Id, RefreshTokenRevocation.AdminRevoked, now, ct);
            return Result.Success();
        }, ct);

    /// <summary>Queues a password reset email (Notifications issues the single-use link; the admin never sees it).</summary>
    public Task<Result<AdminUserDetail>> SendPasswordResetAsync(Guid id, AdminEmailRequest request, CancellationToken ct) =>
        MutateAsync(id, "PasswordReset", request.Reason, (user, _, now) => Task.FromResult(user.RequestPasswordResetByAdmin(now, request.Language)), ct);

    public Task<Result<AdminUserDetail>> ResendVerificationAsync(Guid id, AdminEmailRequest request, CancellationToken ct) =>
        MutateAsync(id, "ResendVerify", request.Reason, (user, _, now) => Task.FromResult(user.RequestEmailVerificationByAdmin(now, request.Language)), ct);

    public Task<Result<AdminUserDetail>> MarkEmailVerifiedAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "MarkVerified", reason, (user, _, now) => Task.FromResult(user.ConfirmEmailByAdmin(reason, now)), ct);

    public Task<Result<AdminUserDetail>> ChangeDisplayNameAsync(Guid id, AdminDisplayNameRequest request, CancellationToken ct) =>
        MutateAsync(id, "DisplayName", request.Reason, (user, _, _) => Task.FromResult(user.ChangeDisplayName(request.DisplayName)), ct,
            snapshot: u => new { u.DisplayName });

    public Task<Result<AdminUserDetail>> GrantPlatformAdminAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "GrantPlatform", reason, (user, _, _) => Task.FromResult(user.PromoteToPlatformAdmin()), ct);

    /// <summary>Refuses self-revoke and revoking the last active platform admin (also enforced by a database trigger).</summary>
    public Task<Result<AdminUserDetail>> RevokePlatformAdminAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "RevokePlatform", reason, async (user, admin, _) =>
        {
            if (AdminReason.Require(reason) is { } invalid)
                return invalid;
            var revoked = user.DemoteFromPlatformAdmin(admin);
            if (!revoked.IsSuccess)
                return revoked;
            return user.IsActive && await IsLastActivePlatformAdminAsync(user.Id, ct) ? LastAdmin() : Result.Success();
        }, ct);

    private async Task<Result<AdminUserDetail>> MutateAsync(Guid id, string operation, string? reason,
        Func<User, Guid, DateTimeOffset, Task<Result>> action, CancellationToken ct, Func<User, object>? snapshot = null)
    {
        if (RequireNoTenant() is { } error)
            return error;
        if (AdminReason.Check(reason) is { } tooLong)
            return tooLong;
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound;

        var admin = currentUser.UserId;
        var now = clock.UtcNow;
        var before = snapshot?.Invoke(user);
        var result = await action(user, admin, now);
        if (!result.IsSuccess)
            return result.Error!;

        AdminAudit.Action(db, clock, admin, nameof(User), user.Id.ToString(), null, operation, reason, before, snapshot?.Invoke(user));
        try
        {
            await db.SaveChangesAsync(ct); // user change + session revocations + audit entry: one transaction
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The account was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (DbErrors.Find(ex)?.ConstraintName == LastAdminConstraint)
        {
            return LastAdmin();
        }

        logger.LogInformation("Platform admin {AdminId} ran {Operation} on user {UserId}", admin, operation, user.Id);
        return await ToDetailAsync(user, ct);
    }

    private Task<bool> IsLastActivePlatformAdminAsync(Guid userId, CancellationToken ct) =>
        db.Set<User>().AllAsync(u => u.Id == userId || !u.IsPlatformAdmin || !u.IsActive, ct);

    private static Error LastAdmin() =>
        Error.Conflict("user.last_platform_admin", "At least one active platform administrator must remain.");

    private async Task<AdminUserDetail> ToDetailAsync(User u, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var sessions = await db.Set<RefreshToken>().AsNoTracking()
            .Where(t => t.UserId == u.Id && t.RevokedAt == null && t.UsedAt == null && t.ExpiresAt > now)
            .Select(t => t.FamilyId).Distinct().CountAsync(ct);
        var byUser = await memberships.GetAsync([u.Id], ct);
        return new AdminUserDetail(u.Id, u.Email, u.DisplayName, u.EmailVerified, u.EmailVerifiedAt, u.CreatedAt, u.LastLoginAt,
            u.FailedLoginCount, u.LockedUntil, u.IsLockedOut(now), !u.IsActive, u.DisabledAt, u.DisabledReason, u.IsPlatformAdmin, sessions,
            u.VerificationEmailRequestedAt, u.PasswordResetRequestedAt, byUser.GetValueOrDefault(u.Id) ?? [], u.Id == currentUser.UserId);
    }

    /// <summary>Platform actions run without a tenant; a tenant header on an admin call is a client error.</summary>
    private Error? RequireNoTenant() => tenant.OrganizationId is null
        ? null
        : Error.Validation("admin.tenant_header", "Platform administration is called without an organization header.");
}
