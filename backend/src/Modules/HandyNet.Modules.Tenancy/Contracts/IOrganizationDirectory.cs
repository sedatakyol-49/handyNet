using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Tenancy.Contracts;

public sealed record OrganizationSummary(Guid Id, string Name, string City, bool IsVerified);

/// <summary>Seller identity as printed on documents such as till receipts.</summary>
public sealed record OrganizationLegalDetails(
    Guid Id,
    string Name,
    string LegalName,
    string Street,
    string PostalCode,
    string City,
    string CountryCode,
    string? VatId);

/// <summary>Public read API of the Tenancy module for other modules.</summary>
public interface IOrganizationDirectory
{
    Task<OrganizationLegalDetails?> GetLegalDetailsAsync(Guid organizationId, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, OrganizationSummary>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct);

    /// <summary>Users of an organization holding one of the given roles (e.g. who receives order notifications).</summary>
    Task<IReadOnlyList<Guid>> GetMemberUserIdsAsync(Guid organizationId, IReadOnlyCollection<MembershipRole> roles, CancellationToken ct);
}
