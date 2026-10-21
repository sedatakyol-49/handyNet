using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Identity.Application;

internal static class PasswordPolicy
{
    public const int MinLength = 10;

    public static Error? Check(string password) =>
        password.Length < MinLength || !password.Any(char.IsLetter) || !password.Any(c => !char.IsLetter(c))
            ? Error.Validation("password.weak",
                $"Password must have at least {MinLength} characters and contain letters and digits or symbols.")
            : null;
}

internal sealed class UserAccounts(
    HandyNetDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<AccountTokenOptions> tokenOptions,
    IClock clock) : IUserAccounts
{
    public async Task<Result<Guid>> StageNewUserAsync(string email, string displayName, string password, string? language, CancellationToken ct)
    {
        if (PasswordPolicy.Check(password) is { } weak)
            return weak;

        var normalized = User.NormalizeEmail(email);
        if (await db.Set<User>().AnyAsync(u => u.Email == normalized, ct))
            return Error.Conflict("user.email_taken", "A user with this email address already exists.");

        var now = clock.UtcNow;
        var user = User.Create(email, displayName, now);
        user.SetPasswordHash(hasher.HashPassword(user, password));
        user.RequestEmailVerification(now, language); // a new user is never verified or within the cooldown
        db.Add(user);
        return user.Id;
    }

    public async Task<UserSummary?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = User.NormalizeEmail(email);
        return await db.Set<User>().AsNoTracking()
            .Where(u => u.Email == normalized && u.IsActive)
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.IsPlatformAdmin, u.EmailVerifiedAt != null))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        await db.Set<User>().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.IsPlatformAdmin, u.EmailVerifiedAt != null, u.IsActive))
            .ToDictionaryAsync(u => u.Id, ct);

    public async Task<IssuedAccountToken?> IssueEmailVerificationTokenAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || user.EmailVerified)
            return null;
        return await AccountTokenIssuer.IssueAsync(db, user, AccountTokenPurpose.EmailVerification,
            tokenOptions.Value.EmailVerification(), clock.UtcNow, ct);
    }

    public async Task<IssuedAccountToken?> IssuePasswordResetTokenAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return null;
        return await AccountTokenIssuer.IssueAsync(db, user, AccountTokenPurpose.PasswordReset,
            tokenOptions.Value.PasswordReset(), clock.UtcNow, ct);
    }

    public async Task<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct)
    {
        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || string.IsNullOrEmpty(password))
            return false;
        return hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }
}
