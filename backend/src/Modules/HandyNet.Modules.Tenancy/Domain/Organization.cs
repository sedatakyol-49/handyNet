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
    public const int MaxTaxNumberLength = 30;
    public const int MaxContactEmailLength = 320;
    public const int MaxContactPhoneLength = 40;
    public const int MaxWebsiteLength = 300;

    private readonly List<OrganizationModule> _modules = [];
    private readonly List<OrganizationStatusChange> _statusChanges = [];

    private Organization() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string LegalName { get; private set; } = null!;
    public string? VatId { get; private set; }

    /// <summary>Steuernummer of the tax office (optional; maintained by the platform admin).</summary>
    public string? TaxNumber { get; private set; }

    public string Street { get; private set; } = null!;
    public string PostalCode { get; private set; } = null!;
    public string City { get; private set; } = null!;
    public string CountryCode { get; private set; } = null!;
    public string? ContactEmail { get; private set; }
    public string? ContactPhone { get; private set; }
    public string? Website { get; private set; }
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

    /// <summary>New status changes of this unit of work (written append-only; read the history from its own table).</summary>
    public IReadOnlyCollection<OrganizationStatusChange> StatusChanges => _statusChanges;

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
        organization._statusChanges.Add(new OrganizationStatusChange(organization.Id, null, OrganizationStatus.PendingVerification, null, createdBy, now));
        return organization;
    }

    private void Apply(OrganizationProfile p)
    {
        Name = p.Name.Trim();
        LegalName = p.LegalName.Trim();
        VatId = string.IsNullOrWhiteSpace(p.VatId) ? null : Text.NormalizeKey(p.VatId);
        TaxNumber = Blank(p.TaxNumber);
        Street = p.Street.Trim();
        PostalCode = p.PostalCode.Trim();
        City = p.City.Trim();
        CountryCode = p.CountryCode.Trim().ToUpperInvariant();
        ContactEmail = Blank(p.ContactEmail)?.ToLowerInvariant();
        ContactPhone = Blank(p.ContactPhone);
        Website = Blank(p.Website);
        IsWholesaler = p.IsWholesaler;
        IsRetailer = p.IsRetailer;
        IsRepairProvider = p.IsRepairProvider;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The master data as entered (for audit before/after and the admin edit form).</summary>
    public OrganizationProfile Profile => new(Name, LegalName, VatId, Street, PostalCode, City, CountryCode, IsWholesaler, IsRetailer,
        IsRepairProvider, TaxNumber, ContactEmail, ContactPhone, Website);

    /// <summary>
    /// Platform admin: corrects the master data (same rules as the registration, checked by the caller's validator). At least
    /// one business type stays selected (also a check constraint).
    /// </summary>
    public Result UpdateProfile(OrganizationProfile profile)
    {
        if (!(profile.IsWholesaler || profile.IsRetailer || profile.IsRepairProvider))
            return Error.Validation("organization.capability_required", "Select at least one business type.");
        if (profile.Normalized() == Profile.Normalized())
            return Error.Conflict("organization.unchanged", "The master data is unchanged.");
        Apply(profile);
        return Result.Success();
    }

    public Result Verify(Guid adminId, DateTimeOffset now)
    {
        if (Status is not (OrganizationStatus.PendingVerification or OrganizationStatus.Suspended))
            return Error.Conflict("organization.invalid_status", $"Cannot verify an organization in status {Status}.");

        ChangeStatus(OrganizationStatus.Verified, null, adminId, now);
        VerifiedAt = now;
        VerifiedBy = adminId;
        Raise(new OrganizationVerified(Id, adminId, now));
        return Result.Success();
    }

    public Result Reject(string reason, Guid adminId, DateTimeOffset now)
    {
        if (AdminReason.Require(reason) is { } invalid)
            return invalid;
        if (Status != OrganizationStatus.PendingVerification)
            return Error.Conflict("organization.invalid_status", $"Cannot reject an organization in status {Status}.");
        ChangeStatus(OrganizationStatus.Rejected, reason.Trim(), adminId, now);
        return Result.Success();
    }

    public Result Suspend(string reason, Guid adminId, DateTimeOffset now)
    {
        if (AdminReason.Require(reason) is { } invalid)
            return invalid;
        if (Status != OrganizationStatus.Verified)
            return Error.Conflict("organization.invalid_status", $"Cannot suspend an organization in status {Status}.");
        ChangeStatus(OrganizationStatus.Suspended, reason.Trim(), adminId, now);
        return Result.Success();
    }

    /// <summary>Lifts a suspension (back to Verified). Unlike a first verification it sends no "verified" email.</summary>
    public Result Reactivate(string? note, Guid adminId, DateTimeOffset now)
    {
        if (AdminReason.Check(note) is { } invalid)
            return invalid;
        if (Status != OrganizationStatus.Suspended)
            return Error.Conflict("organization.invalid_status", $"Only a suspended organization can be reactivated (status {Status}).");
        ChangeStatus(OrganizationStatus.Verified, AdminReason.Normalize(note), adminId, now);
        StatusReason = null;
        return Result.Success();
    }

    private void ChangeStatus(OrganizationStatus to, string? reason, Guid adminId, DateTimeOffset now)
    {
        _statusChanges.Add(new OrganizationStatusChange(Id, Status, to, reason, adminId, now));
        Status = to;
        StatusReason = to == OrganizationStatus.Verified ? null : reason;
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
    bool IsRepairProvider,
    string? TaxNumber = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Website = null)
{
    /// <summary>Same normalization as stored values, to detect "nothing changed".</summary>
    internal OrganizationProfile Normalized()
    {
        static string? Opt(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new OrganizationProfile(Name.Trim(), LegalName.Trim(), string.IsNullOrWhiteSpace(VatId) ? null : Text.NormalizeKey(VatId),
            Street.Trim(), PostalCode.Trim(), City.Trim(), CountryCode.Trim().ToUpperInvariant(), IsWholesaler, IsRetailer, IsRepairProvider,
            Opt(TaxNumber), Opt(ContactEmail)?.ToLowerInvariant(), Opt(ContactPhone), Opt(Website));
    }
}

/// <summary>
/// One status transition of an organization (registration, verification, rejection, suspension, reactivation): who, when,
/// why. Append-only — the app role may insert and read, never update or delete.
/// </summary>
public sealed class OrganizationStatusChange
{
    private OrganizationStatusChange() { }

    internal OrganizationStatusChange(Guid organizationId, OrganizationStatus? from, OrganizationStatus to, string? reason, Guid? changedBy, DateTimeOffset at)
    {
        Id = Guid.CreateVersion7();
        OrganizationId = organizationId;
        FromStatus = from;
        ToStatus = to;
        Reason = reason;
        ChangedBy = changedBy;
        ChangedAt = at;
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Null for the registration (first entry).</summary>
    public OrganizationStatus? FromStatus { get; private set; }

    public OrganizationStatus ToStatus { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>Admin (or, for the registration, the registering user). Null for history backfilled without an author.</summary>
    public Guid? ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }
}

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

    /// <summary>
    /// Staff only: the member's POS rights (subset of <see cref="Permissions.ConfigurablePos"/>), replacing the role defaults.
    /// Null = role defaults. Owners and managers always hold every POS right.
    /// </summary>
    public List<string>? PosPermissions { get; private set; }

    /// <summary>Staff only: largest line discount in percent (0–100). Null = no limit (100).</summary>
    public int? PosMaxDiscountPercent { get; private set; }

    /// <summary>Sets the per-cashier POS rights of a staff member. Null values return to the role defaults.</summary>
    public Result SetPosRights(IReadOnlyCollection<string>? permissions, int? maxDiscountPercent)
    {
        if (Role != MembershipRole.Staff)
            return Error.Conflict("staff.pos_rights_role", "Owners and managers always hold every POS right; only staff rights can be configured.");
        if (permissions?.FirstOrDefault(p => !Permissions.ConfigurablePos.Contains(p)) is { } unknown)
            return Error.Validation("staff.pos_permission_unknown",
                $"'{unknown}' is not a configurable POS permission ({string.Join(", ", Permissions.ConfigurablePos.Order())}).");
        if (maxDiscountPercent is < 0 or > Permissions.FullDiscountPercent)
            return Error.Validation("staff.max_discount", "The maximum discount must be between 0 and 100 percent.");
        PosPermissions = permissions?.Distinct().Order().ToList();
        PosMaxDiscountPercent = maxDiscountPercent;
        return Result.Success();
    }

    /// <summary>
    /// Changes the role. Leaving "Staff" drops the per-cashier POS configuration (owners and managers hold every POS right).
    /// Whether an owner remains is checked by <see cref="MembershipRules"/> and the database.
    /// </summary>
    public Result ChangeRole(MembershipRole role)
    {
        if (!Enum.IsDefined(role))
            return Error.Validation("membership.role_unknown", "Unknown role.");
        if (role == Role)
            return Error.Conflict("membership.role_unchanged", "The member already has this role.");
        Role = role;
        if (role != MembershipRole.Staff)
        {
            PosPermissions = null;
            PosMaxDiscountPercent = null;
        }
        return Result.Success();
    }

    public static Membership Create(Guid organizationId, Guid userId, MembershipRole role, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        UserId = userId,
        Role = role,
        CreatedAt = now,
    };
}

/// <summary>Format rules for the optional contact fields of an organization.</summary>
public static class OrganizationContactRules
{
    /// <summary>German Steuernummer: 10–13 digits (10/11 in the tax office format, 13 in the federal format), separators / or space.</summary>
    public static bool IsTaxNumber(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > Organization.MaxTaxNumberLength || trimmed.Any(c => !(char.IsAsciiDigit(c) || c is '/' or ' ')))
            return false;
        var digits = trimmed.Count(char.IsAsciiDigit);
        return digits is >= 10 and <= 13 && char.IsAsciiDigit(trimmed[0]) && char.IsAsciiDigit(trimmed[^1]);
    }

    /// <summary>An absolute http(s) URL with a host that contains a dot (e.g. https://www.example.de).</summary>
    public static bool IsWebsite(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Contains('.')
        && !value.Trim().Any(char.IsWhiteSpace);
}

/// <summary>Invariants over all memberships of one organization. The database enforces the same rule (deferred trigger).</summary>
public static class MembershipRules
{
    public static readonly Error LastOwner = Error.Conflict("organization.last_owner",
        "An organization must keep at least one owner. Transfer the ownership to another member first.");

    /// <summary>Role change of one member; refuses to demote the last owner.</summary>
    public static Result ChangeRole(IReadOnlyCollection<Membership> members, Guid userId, MembershipRole role)
    {
        var member = members.FirstOrDefault(m => m.UserId == userId);
        if (member is null)
            return Error.NotFound("membership.not_found", "The user is not a member of this organization.");
        if (member.Role == MembershipRole.Owner && role != MembershipRole.Owner && OwnerCount(members) == 1)
            return LastOwner;
        return member.ChangeRole(role);
    }

    /// <summary>Checks that <paramref name="userId"/> may be removed (the last owner may not).</summary>
    public static Result CanRemove(IReadOnlyCollection<Membership> members, Guid userId)
    {
        var member = members.FirstOrDefault(m => m.UserId == userId);
        if (member is null)
            return Error.NotFound("membership.not_found", "The user is not a member of this organization.");
        return member.Role == MembershipRole.Owner && OwnerCount(members) == 1 ? LastOwner : Result.Success();
    }

    /// <summary>Makes <paramref name="toUserId"/> the only owner; previous owners become managers.</summary>
    public static Result TransferOwnership(IReadOnlyCollection<Membership> members, Guid toUserId)
    {
        var target = members.FirstOrDefault(m => m.UserId == toUserId);
        if (target is null)
            return Error.NotFound("membership.not_found", "The new owner must already be a member of this organization.");
        if (target.Role == MembershipRole.Owner && OwnerCount(members) == 1)
            return Error.Conflict("membership.already_owner", "This member is already the owner.");

        if (target.Role != MembershipRole.Owner)
            target.ChangeRole(MembershipRole.Owner);
        foreach (var previous in members.Where(m => m.Role == MembershipRole.Owner && m.UserId != toUserId))
            previous.ChangeRole(MembershipRole.Manager);
        return Result.Success();
    }

    private static int OwnerCount(IEnumerable<Membership> members) => members.Count(m => m.Role == MembershipRole.Owner);
}
