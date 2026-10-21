using HandyNet.Modules.Identity.Contracts;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Identity.Domain;

public sealed class User : Entity, IAuditable
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Minimum time between two verification or two password reset emails for the same user.</summary>
    public static readonly TimeSpan AccountEmailCooldown = TimeSpan.FromMinutes(1);

    private User() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public bool IsPlatformAdmin { get; private set; }
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public DateTimeOffset? VerificationEmailRequestedAt { get; private set; }
    public DateTimeOffset? PasswordResetRequestedAt { get; private set; }

    /// <summary>When a platform admin disabled the account (null = enabled). Disabled users cannot sign in, refresh or use a token.</summary>
    public DateTimeOffset? DisabledAt { get; private set; }

    public string? DisabledReason { get; private set; }
    public uint Version { get; private set; }

    public const int MaxDisplayNameLength = 200;

    public bool EmailVerified => EmailVerifiedAt is not null;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Create(string email, string displayName, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Email = NormalizeEmail(email),
        DisplayName = displayName.Trim(),
        IsActive = true,
        CreatedAt = now,
    };

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void GrantPlatformAdmin() => IsPlatformAdmin = true;

    /// <summary>Platform admin: grant the platform-admin role. Fails if the user already has it or is disabled.</summary>
    public Result PromoteToPlatformAdmin()
    {
        if (!IsActive)
            return Error.Conflict("user.disabled", "A disabled account cannot become a platform administrator.");
        if (IsPlatformAdmin)
            return Error.Conflict("user.already_platform_admin", "The user is already a platform administrator.");
        IsPlatformAdmin = true;
        return Result.Success();
    }

    /// <summary>
    /// Platform admin: take the platform-admin role away. Nobody can take it from themselves; that at least one active
    /// platform admin remains is checked by the caller and enforced by the database (trigger on identity.users).
    /// </summary>
    public Result DemoteFromPlatformAdmin(Guid actingAdminId)
    {
        if (actingAdminId == Id)
            return Error.Conflict("user.self_admin_revoke", "You cannot revoke your own platform administrator role.");
        if (!IsPlatformAdmin)
            return Error.Conflict("user.not_platform_admin", "The user is not a platform administrator.");
        IsPlatformAdmin = false;
        return Result.Success();
    }

    /// <summary>
    /// Platform admin: disable the account. The user can no longer sign in, refresh or use an access token; the caller
    /// revokes all sessions in the same transaction. Admins cannot disable themselves.
    /// </summary>
    public Result Disable(Guid actingAdminId, string? reason, DateTimeOffset now)
    {
        if (actingAdminId == Id)
            return Error.Conflict("user.self_disable", "You cannot disable your own account.");
        if (AdminReason.Require(reason) is { } invalid)
            return invalid;
        if (!IsActive)
            return Error.Conflict("user.already_disabled", "The account is already disabled.");
        IsActive = false;
        DisabledAt = now;
        DisabledReason = reason!.Trim();
        return Result.Success();
    }

    /// <summary>Platform admin: enable a disabled account again (lockout and failed attempts are reset as well).</summary>
    public Result Enable()
    {
        if (IsActive)
            return Error.Conflict("user.not_disabled", "The account is not disabled.");
        IsActive = true;
        DisabledAt = null;
        DisabledReason = null;
        FailedLoginCount = 0;
        LockedUntil = null;
        return Result.Success();
    }

    /// <summary>Platform admin: lift a temporary lockout after too many failed sign-ins.</summary>
    public Result ClearLockout(DateTimeOffset now)
    {
        if (!IsLockedOut(now) && FailedLoginCount == 0)
            return Error.Conflict("user.not_locked_out", "The account is not locked and has no failed sign-in attempts.");
        FailedLoginCount = 0;
        LockedUntil = null;
        return Result.Success();
    }

    public Result ChangeDisplayName(string? displayName)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxDisplayNameLength)
            return Error.Validation("user.display_name", $"A display name (max {MaxDisplayNameLength} characters) is required.");
        if (name == DisplayName)
            return Error.Conflict("user.display_name_unchanged", "The display name is unchanged.");
        DisplayName = name;
        return Result.Success();
    }

    /// <summary>Platform admin: confirm the email address without a link (e.g. confirmed by phone). The reason is required and audited.</summary>
    public Result ConfirmEmailByAdmin(string? reason, DateTimeOffset now)
    {
        if (AdminReason.Require(reason) is { } invalid)
            return invalid;
        if (EmailVerified)
            return Error.Conflict("auth.email_already_verified", "The email address is already verified.");
        EmailVerifiedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Platform admin: send a password reset link. Unlike the anonymous request the outcome is reported (the caller is
    /// trusted): a disabled account or the cooldown give a clear error.
    /// </summary>
    public Result RequestPasswordResetByAdmin(DateTimeOffset now, string? language)
    {
        if (!IsActive)
            return Error.Conflict("user.disabled", "The account is disabled. Enable it before sending a password reset link.");
        return RequestPasswordReset(now, language)
            ? Result.Success()
            : Error.Conflict("auth.reset_recently_sent", "A password reset email was sent a moment ago. Please wait a minute.");
    }

    /// <summary>Platform admin: resend the verification link (same rules as the user's own "resend"; disabled accounts get none).</summary>
    public Result RequestEmailVerificationByAdmin(DateTimeOffset now, string? language) =>
        IsActive
            ? RequestEmailVerification(now, language)
            : Error.Conflict("user.disabled", "The account is disabled. Enable it before sending a verification link.");

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockedUntil = now + LockoutDuration;
            FailedLoginCount = 0;
        }
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    /// <summary>
    /// Asks for a verification email (registration or "resend"). Fails when the address is already verified or a
    /// verification email was requested less than <see cref="AccountEmailCooldown"/> ago.
    /// </summary>
    public Result RequestEmailVerification(DateTimeOffset now, string? language)
    {
        if (EmailVerified)
            return Error.Conflict("auth.email_already_verified", "The email address is already verified.");
        if (VerificationEmailRequestedAt is { } last && now - last < AccountEmailCooldown)
            return Error.Conflict("auth.verification_recently_sent",
                "A verification email was sent a moment ago. Please wait a minute before requesting another one.");

        VerificationEmailRequestedAt = now;
        Raise(new EmailVerificationRequested(Id, EmailLanguage.Normalize(language)));
        return Result.Success();
    }

    /// <summary>
    /// Asks for a password reset email. Returns false (and sends nothing) for inactive users or within the cooldown;
    /// callers must answer the same way in every case so the response does not reveal whether the account exists.
    /// </summary>
    public bool RequestPasswordReset(DateTimeOffset now, string? language)
    {
        if (!IsActive)
            return false;
        if (PasswordResetRequestedAt is { } last && now - last < AccountEmailCooldown)
            return false;

        PasswordResetRequestedAt = now;
        Raise(new PasswordResetRequested(Id, EmailLanguage.Normalize(language)));
        return true;
    }

    public void MarkEmailVerified(DateTimeOffset now) => EmailVerifiedAt ??= now;

    /// <summary>
    /// Sets a new password from a reset link: lifts a lockout, and — since the link proved control of the
    /// mailbox — marks the email address verified. Refresh tokens are revoked by the caller.
    /// </summary>
    public void ResetPassword(string newHash, DateTimeOffset now)
    {
        PasswordHash = newHash;
        FailedLoginCount = 0;
        LockedUntil = null;
        MarkEmailVerified(now);
    }
}
