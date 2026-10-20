using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Tenancy.Application;

public sealed record OrganizationInput(
    string Name,
    string LegalName,
    string? VatId,
    string Street,
    string PostalCode,
    string City,
    string? CountryCode,
    bool IsWholesaler,
    bool IsRetailer,
    bool IsRepairProvider)
{
    public OrganizationProfile ToProfile() =>
        new(Name, LegalName, VatId, Street, PostalCode, City, CountryCode ?? "DE", IsWholesaler, IsRetailer, IsRepairProvider);
}

/// <param name="Language">Language of the verification email ("de" default, "en").</param>
public sealed record RegisterBusinessRequest(string Email, string DisplayName, string Password, OrganizationInput Organization, string? Language = null);

public sealed record AddStaffRequest(string Email, string DisplayName, string? Password, MembershipRole Role);

public sealed record MembershipDto(
    Guid OrganizationId,
    string OrganizationName,
    string Role,
    string Status,
    string[] Modules,
    string[] Permissions,
    bool IsWholesaler,
    bool IsRetailer,
    bool IsRepairProvider,
    int MaxDiscountPercent);

/// <summary>Per-cashier POS rights of a staff member. Null values = role defaults.</summary>
public sealed record SetPosRightsRequest(IReadOnlyList<string>? Permissions, int? MaxDiscountPercent);

public sealed record StaffDto(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string>? PosPermissions,
    int? PosMaxDiscountPercent,
    string[] EffectivePermissions,
    int MaxDiscountPercent);

internal sealed class SetPosRightsValidator : AbstractValidator<SetPosRightsRequest>
{
    public SetPosRightsValidator()
    {
        RuleFor(x => x.Permissions).Must(p => p is null || p.Count <= 20).WithMessage("Too many permissions.");
        RuleFor(x => x.MaxDiscountPercent).InclusiveBetween(0, Permissions.FullDiscountPercent).When(x => x.MaxDiscountPercent is not null);
    }
}

public sealed record MeResponse(Guid UserId, string Email, string DisplayName, bool IsPlatformAdmin, bool EmailVerified, IReadOnlyList<MembershipDto> Memberships);

public sealed record RegistrationResult(Guid UserId, Guid OrganizationId);

internal sealed class OrganizationInputValidator : AbstractValidator<OrganizationInput>
{
    public OrganizationInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Street).NotEmpty().MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.CountryCode).Length(2).When(x => x.CountryCode is not null);
        RuleFor(x => x.PostalCode).NotEmpty().Matches(@"^\d{5}$").When(x => (x.CountryCode ?? "DE") == "DE")
            .WithMessage("German postal codes have five digits.");
        RuleFor(x => x.VatId).Matches(@"^DE\s?\d{9}$").When(x => !string.IsNullOrWhiteSpace(x.VatId) && (x.CountryCode ?? "DE") == "DE")
            .WithMessage("German USt-IdNr. must be DE followed by nine digits.");
        RuleFor(x => x).Must(x => x.IsWholesaler || x.IsRetailer || x.IsRepairProvider)
            .OverridePropertyName("Capabilities").WithMessage("Select at least one business type.");
    }
}

internal sealed class RegisterBusinessValidator : AbstractValidator<RegisterBusinessRequest>
{
    public RegisterBusinessValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Organization).NotNull().SetValidator(new OrganizationInputValidator());
        RuleFor(x => x.Language).Must(EmailLanguage.IsSupported).WithMessage("Language must be 'de' or 'en'.");
    }
}

internal sealed class AddStaffValidator : AbstractValidator<AddStaffRequest>
{
    public AddStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Role).IsInEnum().NotEqual(MembershipRole.Owner).WithMessage("Owners cannot be added as staff.");
    }
}

internal sealed class OrganizationService(
    HandyNetDbContext db,
    IUserAccounts users,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IClock clock)
{
    public async Task<Result<RegistrationResult>> RegisterBusinessAsync(RegisterBusinessRequest request, CancellationToken ct)
    {
        var staged = await users.StageNewUserAsync(request.Email, request.DisplayName, request.Password, request.Language, ct);
        if (!staged.IsSuccess)
            return staged.Error!;

        var organizationId = await StageOrganizationAsync(request.Organization, staged.Value, ct);
        if (!organizationId.IsSuccess)
            return organizationId.Error!;

        if (await SaveAsync(ct) is { } conflict)
            return conflict;
        return new RegistrationResult(staged.Value, organizationId.Value);
    }

    /// <summary>An existing user registers an additional business (e.g. a second shop).</summary>
    public async Task<Result<Guid>> RegisterAdditionalAsync(OrganizationInput input, CancellationToken ct)
    {
        var organizationId = await StageOrganizationAsync(input, currentUser.UserId, ct);
        if (!organizationId.IsSuccess)
            return organizationId;
        if (await SaveAsync(ct) is { } conflict)
            return conflict;
        return organizationId;
    }

    private async Task<Result<Guid>> StageOrganizationAsync(OrganizationInput input, Guid ownerId, CancellationToken ct)
    {
        var organization = Organization.Register(input.ToProfile(), ownerId, clock.UtcNow);
        if (organization.VatId is { } vat && await db.Set<Organization>().AnyAsync(o => o.VatId == vat, ct))
            return Error.Conflict("organization.vat_taken", "An organization with this VAT ID is already registered.");

        db.Add(organization);
        db.Add(Membership.Create(organization.Id, ownerId, MembershipRole.Owner, clock.UtcNow));
        return organization.Id;
    }

    private async Task<Error?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("registration.duplicate", "Email address or VAT ID is already registered.");
        }
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var user = (await users.GetAsync([userId], ct))[userId];

        var own = await db.Set<Membership>().AsNoTracking()
            .Where(m => m.UserId == userId)
            .ToDictionaryAsync(m => m.OrganizationId, ct);
        var roles = own.ToDictionary(m => m.Key, m => m.Value.Role);
        var organizationIds = roles.Keys.ToList();
        var organizations = await db.Set<Organization>().AsNoTracking()
            .Include(o => o.Modules)
            .Where(o => organizationIds.Contains(o.Id))
            .OrderBy(o => o.Name)
            .ToListAsync(ct);

        var memberships = organizations.Select(o => new MembershipDto(
                o.Id,
                o.Name,
                roles[o.Id].ToString(),
                o.Status.ToString(),
                o.Status == OrganizationStatus.Verified ? o.Modules.Select(m => m.Module.ToString()).Order().ToArray() : [],
                Permissions.For(roles[o.Id], own[o.Id].PosPermissions).Order().ToArray(),
                o.IsWholesaler,
                o.IsRetailer,
                o.IsRepairProvider,
                Permissions.MaxDiscountPercentFor(roles[o.Id], own[o.Id].PosMaxDiscountPercent)))
            .ToList();

        return new MeResponse(user.Id, user.Email, user.DisplayName, user.IsPlatformAdmin, user.EmailVerified, memberships);
    }

    public async Task<Result<Guid>> AddStaffAsync(AddStaffRequest request, CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        // Existing accounts are never attached to an organization without their consent. Until invitations
        // exist (roadmap), staff accounts can only be created new.
        if (await users.FindByEmailAsync(request.Email, ct) is not null)
            return Error.Conflict("staff.invitation_required",
                "This email address already has a SEHA account. Existing accounts can only join through an invitation (not yet available).");
        if (string.IsNullOrEmpty(request.Password))
            return Error.Validation("staff.password_required", "A new staff account needs an initial password.");

        var staged = await users.StageNewUserAsync(request.Email, request.DisplayName, request.Password, language: null, ct);
        if (!staged.IsSuccess)
            return staged.Error!;
        var userId = staged.Value;

        db.Add(Membership.Create(organizationId, userId, request.Role, clock.UtcNow));
        if (await SaveAsync(ct) is { } conflict)
            return conflict;
        return userId;
    }

    public async Task<IReadOnlyList<StaffDto>> ListStaffAsync(CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        var memberships = await db.Set<Membership>().AsNoTracking()
            .Where(m => m.OrganizationId == organizationId)
            .ToListAsync(ct);
        var people = await users.GetAsync(memberships.Select(m => m.UserId).ToList(), ct);
        return memberships.Select(m => ToStaffDto(m, people[m.UserId])).ToList();
    }

    /// <summary>Per-cashier POS rights (staff only). Takes effect with the member's next request.</summary>
    public async Task<Result<StaffDto>> SetPosRightsAsync(Guid userId, SetPosRightsRequest request, CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        var membership = await db.Set<Membership>().FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);
        if (membership is null)
            return Error.NotFound("staff.not_found", "Staff member not found.");
        var updated = membership.SetPosRights(request.Permissions, request.MaxDiscountPercent);
        if (!updated.IsSuccess)
            return updated.Error!;
        await db.SaveChangesAsync(ct);
        return ToStaffDto(membership, (await users.GetAsync([userId], ct))[userId]);
    }

    private static StaffDto ToStaffDto(Membership m, UserSummary user) =>
        new(m.UserId, user.Email, user.DisplayName, m.Role.ToString(), m.CreatedAt, m.PosPermissions, m.PosMaxDiscountPercent,
            Permissions.For(m.Role, m.PosPermissions).Order().ToArray(), Permissions.MaxDiscountPercentFor(m.Role, m.PosMaxDiscountPercent));
}

internal sealed class OrganizationDirectory(HandyNetDbContext db) : IOrganizationDirectory
{
    public async Task<OrganizationLegalDetails?> GetLegalDetailsAsync(Guid organizationId, CancellationToken ct) =>
        await db.Set<Organization>().AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new OrganizationLegalDetails(o.Id, o.Name, o.LegalName, o.Street, o.PostalCode, o.City, o.CountryCode, o.VatId))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, OrganizationSummary>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct) =>
        await db.Set<Organization>().AsNoTracking()
            .Where(o => organizationIds.Contains(o.Id))
            .Select(o => new OrganizationSummary(o.Id, o.Name, o.City, o.Status == OrganizationStatus.Verified))
            .ToDictionaryAsync(o => o.Id, ct);

    public async Task<IReadOnlyList<Guid>> GetMemberUserIdsAsync(Guid organizationId, IReadOnlyCollection<MembershipRole> roles, CancellationToken ct) =>
        await db.Set<Membership>().AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && roles.Contains(m.Role))
            .Select(m => m.UserId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MemberAccess>> GetMembersAccessAsync(Guid organizationId, CancellationToken ct) =>
        (await db.Set<Membership>().AsNoTracking().Where(m => m.OrganizationId == organizationId)
            .Select(m => new { m.UserId, m.Role, m.PosPermissions, m.PosMaxDiscountPercent }).ToListAsync(ct))
        .Select(m => Access(m.UserId, m.Role, m.PosPermissions, m.PosMaxDiscountPercent)).ToList();

    public async Task<MemberAccess?> GetMemberAccessAsync(Guid organizationId, Guid userId, CancellationToken ct)
    {
        var m = await db.Set<Membership>().AsNoTracking().Where(x => x.OrganizationId == organizationId && x.UserId == userId)
            .Select(x => new { x.UserId, x.Role, x.PosPermissions, x.PosMaxDiscountPercent }).FirstOrDefaultAsync(ct);
        return m is null ? null : Access(m.UserId, m.Role, m.PosPermissions, m.PosMaxDiscountPercent);
    }

    private static MemberAccess Access(Guid userId, MembershipRole role, List<string>? pos, int? maxDiscount) =>
        new(userId, role, Permissions.For(role, pos), Permissions.MaxDiscountPercentFor(role, maxDiscount));
}
