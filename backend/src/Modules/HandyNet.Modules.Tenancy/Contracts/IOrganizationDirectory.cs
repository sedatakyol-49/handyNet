using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Tenancy.Contracts;

public sealed record OrganizationSummary(Guid Id, string Name, string City, bool IsVerified);

/// <summary>Public read API of the Tenancy module for other modules.</summary>
public interface IOrganizationDirectory
{
    Task<IReadOnlyDictionary<Guid, OrganizationSummary>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct);

    /// <summary>Users of an organization holding one of the given roles (e.g. who receives order notifications).</summary>
    Task<IReadOnlyList<Guid>> GetMemberUserIdsAsync(Guid organizationId, IReadOnlyCollection<MembershipRole> roles, CancellationToken ct);
}
