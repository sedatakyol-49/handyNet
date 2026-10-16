using HandyNet.SharedKernel;

namespace HandyNet.Modules.Identity.Contracts;

public sealed record UserSummary(Guid Id, string Email, string DisplayName, bool IsPlatformAdmin, bool EmailVerified = false);

/// <summary>
/// A freshly issued single-use account token (email verification or password reset). <see cref="Token"/> is the
/// only plaintext copy — Identity stores just its SHA-256 hash — so the caller must put it into the email and
/// nowhere else.
/// </summary>
public sealed record IssuedAccountToken(string Token, DateTimeOffset ExpiresAt, TimeSpan ValidFor, UserSummary User);

/// <summary>Public API of the Identity module for other modules.</summary>
public interface IUserAccounts
{
    /// <summary>
    /// Stages a new user in the shared unit of work. The caller commits it together with its own
    /// changes (e.g. organization + owner membership), so registration is atomic. Committing it also
    /// publishes <see cref="EmailVerificationRequested"/>.
    /// </summary>
    /// <param name="language">Language of the verification email (<see cref="EmailLanguage"/>); null = German.</param>
    Task<Result<Guid>> StageNewUserAsync(string email, string displayName, string password, string? language, CancellationToken ct);

    Task<UserSummary?> FindByEmailAsync(string email, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);

    /// <summary>
    /// Stages a new email verification token (older unused ones are revoked) in the shared unit of work; the
    /// caller commits it together with the email it queues. Null when the user is inactive or already verified.
    /// </summary>
    Task<IssuedAccountToken?> IssueEmailVerificationTokenAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Stages a new password reset token (older unused ones are revoked) in the shared unit of work; the caller
    /// commits it together with the email it queues. Null when the user does not exist or is inactive.
    /// </summary>
    Task<IssuedAccountToken?> IssuePasswordResetTokenAsync(Guid userId, CancellationToken ct);
}
