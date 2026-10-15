using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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

internal sealed class UserAccounts(HandyNetDbContext db, IPasswordHasher<User> hasher, IClock clock) : IUserAccounts
{
    public async Task<Result<Guid>> StageNewUserAsync(string email, string displayName, string password, CancellationToken ct)
    {
        if (PasswordPolicy.Check(password) is { } weak)
            return weak;

        var normalized = User.NormalizeEmail(email);
        if (await db.Set<User>().AnyAsync(u => u.Email == normalized, ct))
            return Error.Conflict("user.email_taken", "A user with this email address already exists.");

        var user = User.Create(email, displayName, clock.UtcNow);
        user.SetPasswordHash(hasher.HashPassword(user, password));
        db.Add(user);
        return user.Id;
    }

    public async Task<UserSummary?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = User.NormalizeEmail(email);
        return await db.Set<User>().AsNoTracking()
            .Where(u => u.Email == normalized && u.IsActive)
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.IsPlatformAdmin))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        await db.Set<User>().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.IsPlatformAdmin))
            .ToDictionaryAsync(u => u.Id, ct);
}
