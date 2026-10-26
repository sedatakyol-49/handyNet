using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Identity.Application;

public sealed class AccountTokenOptions
{
    public const string Section = "Identity";

    public int PasswordResetTokenMinutes { get; set; } = (int)AccountToken.DefaultPasswordResetLifetime.TotalMinutes;
    public int EmailVerificationTokenHours { get; set; } = (int)AccountToken.DefaultEmailVerificationLifetime.TotalHours;
}

public sealed record PasswordResetRequest(string Email, string? Language);

public sealed record PasswordResetConfirmRequest(string Token, string NewPassword);

public sealed record VerifyEmailRequest(string Token);

public sealed record ResendVerificationRequest(string? Language);

internal sealed class PasswordResetRequestValidator : AbstractValidator<PasswordResetRequest>
{
    public PasswordResetRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Language).Must(EmailLanguage.IsSupported).WithMessage("Language must be one of de, en, tr, es, ru, ar, fa.").WithErrorCode("validation.language");
    }
}

internal sealed class PasswordResetConfirmRequestValidator : AbstractValidator<PasswordResetConfirmRequest>
{
    public PasswordResetConfirmRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(OpaqueToken.MaxLength);
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(200);
    }
}

internal sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator() => RuleFor(x => x.Token).NotEmpty().MaximumLength(OpaqueToken.MaxLength);
}

internal sealed class ResendVerificationRequestValidator : AbstractValidator<ResendVerificationRequest>
{
    public ResendVerificationRequestValidator() =>
        RuleFor(x => x.Language).Must(EmailLanguage.IsSupported).WithMessage("Language must be one of de, en, tr, es, ru, ar, fa.").WithErrorCode("validation.language");
}

/// <summary>Password reset and email verification. Emails are sent by Notifications from the outbox events.</summary>
internal sealed class AccountRecoveryService(
    HandyNetDbContext db,
    IPasswordHasher<User> hasher,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<AccountRecoveryService> logger)
{
    /// <summary>
    /// Always succeeds with no information: whether the account exists, is inactive or was asked for a link a moment
    /// ago is never visible to the caller (no account enumeration).
    /// </summary>
    public async Task RequestPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null && user.RequestPasswordReset(clock.UtcNow, request.Language))
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Password reset requested for user {UserId}", user.Id);
        }
    }

    /// <summary>
    /// Sets a new password with a reset token: single use, lifts the lockout, verifies the email address and ends
    /// every session (all refresh tokens revoked) — all in one transaction.
    /// </summary>
    public async Task<Result> ResetPasswordAsync(PasswordResetConfirmRequest request, CancellationToken ct)
    {
        // Checked first so a weak password does not burn the link.
        if (PasswordPolicy.Check(request.NewPassword) is { } weak)
            return weak;

        var now = clock.UtcNow;
        var token = await FindAsync(request.Token, AccountTokenPurpose.PasswordReset, ct);
        if (token is null)
            return TokenErrors.LinkInvalid;
        if (token.Use(now) is { IsSuccess: false } used)
            return used;

        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == token.UserId && u.IsActive, ct);
        if (user is null)
            return TokenErrors.LinkInvalid;

        user.ResetPassword(hasher.HashPassword(user, request.NewPassword), now);
        await RevokeOtherOpenTokensAsync(user.Id, AccountTokenPurpose.PasswordReset, token.Id, now, ct);
        await SessionRevocation.StageRevokeAllAsync(db, user.Id, RefreshTokenRevocation.PasswordReset, now, ct);

        if (await SaveOnceAsync(ct) is { } conflict)
            return conflict;
        logger.LogInformation("Password reset completed for user {UserId}", user.Id);
        return Result.Success();
    }

    public async Task<Result> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var token = await FindAsync(request.Token, AccountTokenPurpose.EmailVerification, ct);
        if (token is null)
            return TokenErrors.LinkInvalid;
        if (token.Use(now) is { IsSuccess: false } used)
            return used;

        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == token.UserId && u.IsActive, ct);
        if (user is null)
            return TokenErrors.LinkInvalid;

        user.MarkEmailVerified(now);
        await RevokeOtherOpenTokensAsync(user.Id, AccountTokenPurpose.EmailVerification, token.Id, now, ct);
        return await SaveOnceAsync(ct) is { } conflict ? conflict : Result.Success();
    }

    public async Task<Result> ResendVerificationAsync(ResendVerificationRequest request, CancellationToken ct)
    {
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == currentUser.UserId && u.IsActive, ct);
        if (user is null)
            return TokenErrors.RefreshInvalid;
        var requested = user.RequestEmailVerification(clock.UtcNow, request.Language);
        if (!requested.IsSuccess)
            return requested;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private Task<AccountToken?> FindAsync(string value, AccountTokenPurpose purpose, CancellationToken ct)
    {
        var hash = OpaqueToken.Hash(value);
        return db.Set<AccountToken>().FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose, ct);
    }

    private async Task RevokeOtherOpenTokensAsync(Guid userId, AccountTokenPurpose purpose, Guid keep, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Set<AccountToken>()
            .Where(t => t.UserId == userId && t.Purpose == purpose && t.Id != keep && t.UsedAt == null && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in open)
            t.Revoke(now);
    }

    /// <summary>The token row's version makes a concurrent second use of the same link fail here.</summary>
    private async Task<Error?> SaveOnceAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return TokenErrors.LinkInvalid;
        }
    }
}

internal static class AccountTokenIssuer
{
    /// <summary>Revokes open tokens of the purpose and stages a new one. Not saved; the caller commits.</summary>
    public static async Task<IssuedAccountToken> IssueAsync(HandyNetDbContext db, User user, AccountTokenPurpose purpose,
        TimeSpan lifetime, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Set<AccountToken>()
            .Where(t => t.UserId == user.Id && t.Purpose == purpose && t.UsedAt == null && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in open)
            t.Revoke(now);

        var (token, value) = AccountToken.Issue(user.Id, purpose, now, lifetime);
        db.Add(token);
        return new IssuedAccountToken(value, token.ExpiresAt, lifetime,
            new UserSummary(user.Id, user.Email, user.DisplayName, user.IsPlatformAdmin, user.EmailVerified));
    }
}

internal static class AccountTokenLifetimes
{
    public static TimeSpan PasswordReset(this AccountTokenOptions o) => TimeSpan.FromMinutes(o.PasswordResetTokenMinutes);
    public static TimeSpan EmailVerification(this AccountTokenOptions o) => TimeSpan.FromHours(o.EmailVerificationTokenHours);
}
