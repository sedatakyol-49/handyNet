using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Tenancy.Contracts;

public sealed record OrganizationSummary(Guid Id, string Name, string City, bool IsVerified);

/// <summary>Name, legal name, postal address and VAT id of an organization, as printed on documents it issues (till receipts, Ankaufbeleg).</summary>
public sealed record OrganizationLegalDetails(
    Guid Id,
    string Name,
    string LegalName,
    string Street,
    string PostalCode,
    string City,
    string CountryCode,
    string? VatId);

/// <summary>A member's role and effective permissions in one organization (POS: who may act as cashier or approve).</summary>
public sealed record MemberAccess(Guid UserId, MembershipRole Role, IReadOnlySet<string> Permissions, int MaxDiscountPercent);

/// <summary>Public read API of the Tenancy module for other modules.</summary>
public interface IOrganizationDirectory
{
    /// <summary>Document details of one organization; null if it does not exist.</summary>
    Task<OrganizationLegalDetails?> GetLegalDetailsAsync(Guid organizationId, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, OrganizationSummary>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct);

    /// <summary>Users of an organization holding one of the given roles (e.g. who receives order notifications).</summary>
    Task<IReadOnlyList<Guid>> GetMemberUserIdsAsync(Guid organizationId, IReadOnlyCollection<MembershipRole> roles, CancellationToken ct);

    /// <summary>Role and effective permissions of every member (same rules as the request's tenant context).</summary>
    Task<IReadOnlyList<MemberAccess>> GetMembersAccessAsync(Guid organizationId, CancellationToken ct);

    /// <summary>Role and effective permissions of one member; null if the user is not a member.</summary>
    Task<MemberAccess?> GetMemberAccessAsync(Guid organizationId, Guid userId, CancellationToken ct);
}
