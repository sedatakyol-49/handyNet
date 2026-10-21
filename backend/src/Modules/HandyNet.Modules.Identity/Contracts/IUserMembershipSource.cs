namespace HandyNet.Modules.Identity.Contracts;

/// <summary>One organization a user belongs to, as shown in the platform administration.</summary>
public sealed record UserMembershipInfo(Guid OrganizationId, string OrganizationName, string Role, string OrganizationStatus, DateTimeOffset JoinedAt);

/// <summary>
/// Port for the platform administration of user accounts: Identity knows nothing about organizations, so the module that
/// owns memberships (Tenancy) implements it. Both run in the same unit of work (ADR-0002).
/// </summary>
public interface IUserMembershipSource
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<UserMembershipInfo>>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);

    /// <summary>Ids of users with at least one membership, composable into a query of the same context (filter "no organization").</summary>
    IQueryable<Guid> UserIdsWithMembership();
}
