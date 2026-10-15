using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Tenancy.Domain;

public enum OrganizationStatus
{
    PendingVerification,
    Verified,
    Rejected,
    Suspended,
}

public sealed record OrganizationVerified(Guid OrganizationId, Guid VerifiedBy, DateTimeOffset At) : IDomainEvent;

public sealed record OrganizationModulesChanged(Guid OrganizationId, string[] Modules, Guid ChangedBy) : IDomainEvent;

/// <summary>
/// A registered business. One organization can act as wholesaler, retailer and repair provider at once;
/// capabilities describe what it does, modules (activated by the platform admin) what it may use.
/// </summary>
public sealed class Organization : Entity, IAuditable
{
    private readonly List<OrganizationModule> _modules = [];

    private Organization() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string LegalName { get; private set; } = null!;
    public string? VatId { get; private set; }
    public string Street { get; private set; } = null!;
    public string PostalCode { get; private set; } = null!;
    public string City { get; private set; } = null!;
    public string CountryCode { get; private set; } = null!;
    public bool IsWholesaler { get; private set; }
    public bool IsRetailer { get; private set; }
    public bool IsRepairProvider { get; private set; }
    public OrganizationStatus Status { get; private set; }
    public string? StatusReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public uint Version { get; private set; }

    public IReadOnlyCollection<OrganizationModule> Modules => _modules;

    public static Organization Register(OrganizationProfile profile, Guid createdBy, DateTimeOffset now)
    {
        var organization = new Organization
        {
            Id = Guid.CreateVersion7(),
            Status = OrganizationStatus.PendingVerification,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
        organization.Apply(profile);
        return organization;
    }

    private void Apply(OrganizationProfile p)
    {
        Name = p.Name.Trim();
        LegalName = p.LegalName.Trim();
        VatId = string.IsNullOrWhiteSpace(p.VatId) ? null : Text.NormalizeKey(p.VatId);
        Street = p.Street.Trim();
        PostalCode = p.PostalCode.Trim();
        City = p.City.Trim();
        CountryCode = p.CountryCode.Trim().ToUpperInvariant();
        IsWholesaler = p.IsWholesaler;
        IsRetailer = p.IsRetailer;
        IsRepairProvider = p.IsRepairProvider;
    }

    public Result Verify(Guid adminId, DateTimeOffset now)
    {
        if (Status is not (OrganizationStatus.PendingVerification or OrganizationStatus.Suspended))
            return Error.Conflict("organization.invalid_status", $"Cannot verify an organization in status {Status}.");

        Status = OrganizationStatus.Verified;
        StatusReason = null;
        VerifiedAt = now;
        VerifiedBy = adminId;
        Raise(new OrganizationVerified(Id, adminId, now));
        return Result.Success();
    }

    public Result Reject(string reason)
    {
        if (Status != OrganizationStatus.PendingVerification)
            return Error.Conflict("organization.invalid_status", $"Cannot reject an organization in status {Status}.");
        Status = OrganizationStatus.Rejected;
        StatusReason = reason.Trim();
        return Result.Success();
    }

    public Result Suspend(string reason)
    {
        if (Status != OrganizationStatus.Verified)
            return Error.Conflict("organization.invalid_status", $"Cannot suspend an organization in status {Status}.");
        Status = OrganizationStatus.Suspended;
        StatusReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Replaces the set of enabled modules. Disabled modules are removed; history is kept in the audit log.</summary>
    public void SetModules(IReadOnlySet<PlatformModule> modules, Guid adminId, DateTimeOffset now)
    {
        _modules.RemoveAll(m => !modules.Contains(m.Module));
        foreach (var module in modules.Where(m => _modules.All(x => x.Module != m)))
            _modules.Add(new OrganizationModule(Id, module, adminId, now));

        Raise(new OrganizationModulesChanged(Id, modules.Select(m => m.ToString()).Order().ToArray(), adminId));
    }
}

public sealed record OrganizationProfile(
    string Name,
    string LegalName,
    string? VatId,
    string Street,
    string PostalCode,
    string City,
    string CountryCode,
    bool IsWholesaler,
    bool IsRetailer,
    bool IsRepairProvider);

public sealed class OrganizationModule : IAuditable
{
    private OrganizationModule() { }

    internal OrganizationModule(Guid organizationId, PlatformModule module, Guid enabledBy, DateTimeOffset enabledAt)
    {
        OrganizationId = organizationId;
        Module = module;
        EnabledBy = enabledBy;
        EnabledAt = enabledAt;
    }

    public Guid OrganizationId { get; private set; }
    public PlatformModule Module { get; private set; }
    public Guid EnabledBy { get; private set; }
    public DateTimeOffset EnabledAt { get; private set; }
}

public sealed class Membership : IAuditable
{
    private Membership() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public MembershipRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Membership Create(Guid organizationId, Guid userId, MembershipRole role, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        UserId = userId,
        Role = role,
        CreatedAt = now,
    };
}
