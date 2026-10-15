using HandyNet.SharedKernel;

namespace HandyNet.Modules.Identity.Contracts;

public sealed record UserSummary(Guid Id, string Email, string DisplayName, bool IsPlatformAdmin);

/// <summary>Public API of the Identity module for other modules.</summary>
public interface IUserAccounts
{
    /// <summary>
    /// Stages a new user in the shared unit of work. The caller commits it together with its own
    /// changes (e.g. organization + owner membership), so registration is atomic.
    /// </summary>
    Task<Result<Guid>> StageNewUserAsync(string email, string displayName, string password, CancellationToken ct);

    Task<UserSummary?> FindByEmailAsync(string email, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
}
