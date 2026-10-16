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
    public uint Version { get; private set; }

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
