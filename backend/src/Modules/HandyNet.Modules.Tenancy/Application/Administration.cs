using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Tenancy.Application;

/// <param name="MemberCount">Memberships (all roles).</param>
/// <param name="RegisterCount">POS registers (Kassen), from the POS module.</param>
/// <param name="OfferCount">Marketplace offers (any status), from the Marketplace module.</param>
public sealed record AdminOrganizationDto(
    Guid Id,
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
    string Status,
    string? StatusReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt,
    string[] Modules,
    string? TaxNumber = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Website = null,
    int MemberCount = 0,
    int RegisterCount = 0,
    int OfferCount = 0);

public sealed record AdminPersonDto(Guid Id, string Email, string DisplayName);

/// <param name="From">Null for the registration.</param>
/// <param name="By">Admin who made the change (the registering user for the first entry); null if unknown.</param>
public sealed record AdminStatusChangeDto(string? From, string To, string? Reason, DateTimeOffset At, AdminPersonDto? By);

public sealed record AdminMemberDto(Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset JoinedAt, bool EmailVerified, bool IsDisabled);

/// <param name="Version">Concurrency token of the organization row; send it back with a master-data update.</param>
public sealed record AdminOrganizationDetailDto(
    AdminOrganizationDto Organization,
    uint Version,
    AdminPersonDto? CreatedBy,
    AdminPersonDto? VerifiedBy,
    IReadOnlyList<AdminStatusChangeDto> StatusHistory,
    IReadOnlyList<AdminMemberDto> Members,
    IReadOnlyList<string> Registers,
    int PublishedOfferCount);

/// <param name="Capability">"wholesaler", "retailer" or "repair".</param>
/// <param name="CreatedFrom">Registered at or after (inclusive).</param>
/// <param name="CreatedTo">Registered before (exclusive).</param>
public sealed record AdminOrganizationQuery(
    string? Status,
    string? Search,
    string? Module,
    string? Capability,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    int? Page,
    int? PageSize);

/// <summary>Reason of a status change (required for reject/suspend, optional note for reactivate).</summary>
public sealed record ReasonRequest(string? Reason);

public sealed record SetModulesRequest(string[] Modules);

/// <param name="Version">The <see cref="AdminOrganizationDetailDto.Version"/> the form was loaded with (optimistic concurrency).</param>
public sealed record AdminUpdateOrganizationRequest(OrganizationInput Organization, string? Reason, uint? Version);

public sealed record ChangeMemberRoleRequest(MembershipRole Role, string? Reason);

public sealed record TransferOwnershipRequest(Guid UserId, string? Reason);

internal sealed class AdminOrganizationQueryValidator : AbstractValidator<AdminOrganizationQuery>
{
    public static readonly string[] Capabilities = ["wholesaler", "retailer", "repair"];

    public AdminOrganizationQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Status).Must(s => string.IsNullOrWhiteSpace(s) || Enum.TryParse<OrganizationStatus>(s, true, out _))
            .WithMessage("Unknown organization status.");
        RuleFor(x => x.Module).Must(m => string.IsNullOrWhiteSpace(m) || (Enum.TryParse<PlatformModule>(m, true, out var p) && Enum.IsDefined(p)))
            .WithMessage("Unknown module.");
        RuleFor(x => x.Capability).Must(c => string.IsNullOrWhiteSpace(c) || Capabilities.Contains(c, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Capability must be wholesaler, retailer or repair.");
        RuleFor(x => x).Must(x => x.CreatedFrom is null || x.CreatedTo is null || x.CreatedFrom < x.CreatedTo)
            .OverridePropertyName("CreatedTo").WithMessage("The end of the period must be after its start.");
    }
}

internal sealed class AdminUpdateOrganizationValidator : AbstractValidator<AdminUpdateOrganizationRequest>
{
    public AdminUpdateOrganizationValidator()
    {
        RuleFor(x => x.Organization).NotNull().SetValidator(new OrganizationInputValidator());
        RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
    }
}

internal sealed class ReasonRequestValidator : AbstractValidator<ReasonRequest>
{
    public ReasonRequestValidator() => RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
}

internal sealed class ChangeMemberRoleValidator : AbstractValidator<ChangeMemberRoleRequest>
{
    public ChangeMemberRoleValidator()
    {
        RuleFor(x => x.Role).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
    }
}

internal sealed class TransferOwnershipValidator : AbstractValidator<TransferOwnershipRequest>
{
    public TransferOwnershipValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(AdminReason.MaxLength);
    }
}

/// <summary>
/// Platform administration of companies: master data, status, modules, memberships and counts. Contract/account data only —
/// tenants' operational data (customers, receipts, sales, Ankauf seller data) is never read here. Admin calls run without a
/// tenant; counts of tenant-owned data come from <see cref="IOrganizationFactSource"/> (id-only database functions). Every
/// write is audited with the reason (<see cref="AdminAudit"/>); opening the detail view is access-logged.
/// </summary>
internal sealed class AdministrationService(
    HandyNetDbContext db,
    IUserAccounts users,
    IEnumerable<IOrganizationFactSource> factSources,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IClock clock,
    ILogger<AdministrationService> logger)
{
    public const string OwnerRequiredConstraint = "ck_memberships_owner_required";

    private static readonly Error OrganizationNotFound = Error.NotFound("organization.not_found", "Organization not found.");

    public async Task<Result<PagedResult<AdminOrganizationDto>>> ListAsync(AdminOrganizationQuery q, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var (p, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = db.Set<Organization>().AsNoTracking().Include(o => o.Modules).AsQueryable();

        if (Enum.TryParse<OrganizationStatus>(q.Status, true, out var parsed))
            query = query.Where(o => o.Status == parsed);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = Text.LikePattern(q.Search);
            query = query.Where(o => EF.Functions.ILike(o.Name, pattern) || EF.Functions.ILike(o.LegalName, pattern)
                                     || EF.Functions.ILike(o.City, pattern)
                                     || (o.VatId != null && EF.Functions.ILike(o.VatId, pattern)));
        }
        if (Enum.TryParse<PlatformModule>(q.Module, true, out var module))
            query = query.Where(o => o.Modules.Any(m => m.Module == module));
        query = q.Capability?.ToLowerInvariant() switch
        {
            "wholesaler" => query.Where(o => o.IsWholesaler),
            "retailer" => query.Where(o => o.IsRetailer),
            "repair" => query.Where(o => o.IsRepairProvider),
            _ => query,
        };
        if (q.CreatedFrom is { } from)
            query = query.Where(o => o.CreatedAt >= from);
        if (q.CreatedTo is { } to)
            query = query.Where(o => o.CreatedAt < to);

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(o => o.Status).ThenByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
            .Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AdminOrganizationDto>(await ToDtosAsync(items, ct), p, size, total);
    }

    /// <summary>Everything the platform admin may see about one company; writes an access-log entry.</summary>
    public async Task<Result<AdminOrganizationDetailDto>> GetAsync(Guid id, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var organization = await db.Set<Organization>().AsNoTracking().Include(o => o.Modules).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (organization is null)
            return OrganizationNotFound;
        AdminAudit.Access(db, clock, currentUser.UserId, nameof(Organization), id.ToString(), id, "organization.detail");
        await db.SaveChangesAsync(ct);
        return await ToDetailAsync(organization, ct);
    }

    public async Task<Result<AdminOrganizationDetailDto>> UpdateAsync(Guid id, AdminUpdateOrganizationRequest request, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var organization = await db.Set<Organization>().Include(o => o.Modules).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (organization is null)
            return OrganizationNotFound;
        if (request.Version is { } expected)
            db.Entry(organization).Property(o => o.Version).OriginalValue = expected;

        var profile = request.Organization.ToProfile();
        var before = organization.Profile;
        var updated = organization.UpdateProfile(profile);
        if (!updated.IsSuccess)
            return updated.Error!;
        if (organization.VatId is { } vat && await db.Set<Organization>().AnyAsync(o => o.VatId == vat && o.Id != id, ct))
            return Error.Conflict("organization.vat_taken", "An organization with this VAT ID is already registered.");

        AdminAudit.Action(db, clock, currentUser.UserId, nameof(Organization), id.ToString(), id, "Update", request.Reason,
            before, organization.Profile);
        if (await SaveAsync(ct) is { } conflict)
            return conflict;
        return await ToDetailAsync(organization, ct);
    }

    public Task<Result<AdminOrganizationDto>> VerifyAsync(Guid id, CancellationToken ct) =>
        MutateAsync(id, "Verify", null, o => o.Verify(currentUser.UserId, clock.UtcNow), ct);

    public Task<Result<AdminOrganizationDto>> RejectAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "Reject", reason, o => o.Reject(reason ?? string.Empty, currentUser.UserId, clock.UtcNow), ct);

    public Task<Result<AdminOrganizationDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct) =>
        MutateAsync(id, "Suspend", reason, o => o.Suspend(reason ?? string.Empty, currentUser.UserId, clock.UtcNow), ct);

    public Task<Result<AdminOrganizationDto>> ReactivateAsync(Guid id, string? note, CancellationToken ct) =>
        MutateAsync(id, "Reactivate", note, o => o.Reactivate(note, currentUser.UserId, clock.UtcNow), ct);

    public async Task<Result<AdminOrganizationDto>> SetModulesAsync(Guid id, string[] modules, CancellationToken ct)
    {
        var parsed = new HashSet<PlatformModule>();
        foreach (var name in modules)
        {
            if (!Enum.TryParse<PlatformModule>(name, true, out var module) || !Enum.IsDefined(module))
                return Error.Validation("module.unknown", $"Unknown module '{name}'.");
            parsed.Add(module);
        }

        return await MutateAsync(id, "SetModules", null, o =>
        {
            o.SetModules(parsed, currentUser.UserId, clock.UtcNow);
            return Result.Success();
        }, ct);
    }

    public Task<Result<AdminOrganizationDetailDto>> ChangeMemberRoleAsync(Guid id, Guid userId, ChangeMemberRoleRequest request, CancellationToken ct) =>
        MutateMembersAsync(id, "ChangeRole", request.Reason, userId, members => MembershipRules.ChangeRole(members, userId, request.Role), ct);

    /// <summary>Makes the member the only owner; previous owners become managers (one transaction).</summary>
    public Task<Result<AdminOrganizationDetailDto>> TransferOwnershipAsync(Guid id, TransferOwnershipRequest request, CancellationToken ct) =>
        MutateMembersAsync(id, "TransferOwner", request.Reason, request.UserId, members => MembershipRules.TransferOwnership(members, request.UserId), ct);

    /// <summary>
    /// Removes a membership (the user account stays). The app role has no DELETE on memberships: the row is removed by the
    /// audited function <c>tenancy.admin_remove_membership</c>, which refuses tenant sessions and non-admin callers.
    /// </summary>
    public async Task<Result<AdminOrganizationDetailDto>> RemoveMemberAsync(Guid id, Guid userId, string? reason, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        if (AdminReason.Require(reason) is { } invalid)
            return invalid;
        var organization = await db.Set<Organization>().AsNoTracking().Include(o => o.Modules).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (organization is null)
            return OrganizationNotFound;
        var members = await db.Set<Membership>().AsNoTracking().Where(m => m.OrganizationId == id).ToListAsync(ct);
        var allowed = MembershipRules.CanRemove(members, userId);
        if (!allowed.IsSuccess)
            return allowed.Error!;

        var admin = currentUser.UserId;
        var now = clock.UtcNow;
        var text = AdminReason.Normalize(reason);
        try
        {
            var removed = await db.Database.SqlQuery<bool>($"""
                    SELECT tenancy.admin_remove_membership({id}::uuid, {userId}::uuid, {admin}::uuid, {text}::text, {now}::timestamptz) AS "Value"
                    """)
                .SingleAsync(ct);
            if (!removed)
                return Error.NotFound("membership.not_found", "The user is not a member of this organization.");
        }
        catch (Exception ex) when (DbErrors.Find(ex)?.ConstraintName == OwnerRequiredConstraint)
        {
            return MembershipRules.LastOwner;
        }

        logger.LogInformation("Platform admin {AdminId} removed user {UserId} from organization {OrganizationId}", admin, userId, id);
        return await ToDetailAsync(organization, ct);
    }

    private async Task<Result<AdminOrganizationDetailDto>> MutateMembersAsync(Guid id, string operation, string? reason, Guid userId,
        Func<IReadOnlyCollection<Membership>, Result> action, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        if (AdminReason.Check(reason) is { } tooLong)
            return tooLong;
        var organization = await db.Set<Organization>().AsNoTracking().Include(o => o.Modules).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (organization is null)
            return OrganizationNotFound;
        var members = await db.Set<Membership>().Where(m => m.OrganizationId == id).ToListAsync(ct);
        var before = Roles(members);
        var result = action(members);
        if (!result.IsSuccess)
            return result.Error!;

        AdminAudit.Action(db, clock, currentUser.UserId, nameof(Membership), $"{id},{userId}", id, operation, reason, before, Roles(members));
        if (await SaveAsync(ct) is { } conflict)
            return conflict;
        return await ToDetailAsync(organization, ct);
    }

    private static Dictionary<string, string> Roles(IEnumerable<Membership> members) =>
        members.OrderBy(m => m.UserId).ToDictionary(m => m.UserId.ToString(), m => m.Role.ToString());

    private async Task<Result<AdminOrganizationDto>> MutateAsync(Guid id, string operation, string? reason, Func<Organization, Result> action, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        if (AdminReason.Check(reason) is { } tooLong)
            return tooLong;
        var organization = await db.Set<Organization>().Include(o => o.Modules).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (organization is null)
            return OrganizationNotFound;

        var before = new { Status = organization.Status.ToString(), Modules = ModuleNames(organization) };
        var result = action(organization);
        if (!result.IsSuccess)
            return result.Error!;

        AdminAudit.Action(db, clock, currentUser.UserId, nameof(Organization), id.ToString(), id, operation, reason,
            before, new { Status = organization.Status.ToString(), Modules = ModuleNames(organization) });
        if (await SaveAsync(ct) is { } conflict)
            return conflict;
        return (await ToDtosAsync([organization], ct))[0];
    }

    private static string[] ModuleNames(Organization o) => o.Modules.Select(m => m.Module.ToString()).Order().ToArray();

    private async Task<Error?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The organization was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("organization.vat_taken", "An organization with this VAT ID is already registered.");
        }
        catch (Exception ex) when (DbErrors.Find(ex)?.ConstraintName == OwnerRequiredConstraint)
        {
            return MembershipRules.LastOwner;
        }
    }

    private async Task<IReadOnlyList<AdminOrganizationDto>> ToDtosAsync(IReadOnlyList<Organization> organizations, CancellationToken ct)
    {
        var ids = organizations.Select(o => o.Id).ToList();
        var memberCounts = await db.Set<Membership>().AsNoTracking().Where(m => ids.Contains(m.OrganizationId))
            .GroupBy(m => m.OrganizationId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var facts = await FactsAsync(ids, ct);
        return organizations.Select(o => ToDto(o, memberCounts.GetValueOrDefault(o.Id), facts.GetValueOrDefault(o.Id) ?? OrganizationFacts.Empty))
            .ToList();
    }

    private async Task<Dictionary<Guid, OrganizationFacts>> FactsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, (Dictionary<string, int> Counts, List<string> Registers)>();
        if (ids.Count == 0)
            return [];
        foreach (var source in factSources)
        {
            foreach (var (orgId, f) in await source.GetAsync(ids, ct))
            {
                if (!merged.TryGetValue(orgId, out var m))
                    merged[orgId] = m = (new Dictionary<string, int>(), []);
                foreach (var (key, count) in f.Counts)
                    m.Counts[key] = m.Counts.GetValueOrDefault(key) + count;
                m.Registers.AddRange(f.RegisterNames);
            }
        }
        return merged.ToDictionary(x => x.Key, x => new OrganizationFacts(x.Value.Counts, x.Value.Registers.Order(StringComparer.CurrentCultureIgnoreCase).ToList()));
    }

    private async Task<AdminOrganizationDetailDto> ToDetailAsync(Organization o, CancellationToken ct)
    {
        var history = await db.Set<OrganizationStatusChange>().AsNoTracking().Where(c => c.OrganizationId == o.Id)
            .OrderBy(c => c.ChangedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var members = await db.Set<Membership>().AsNoTracking().Where(m => m.OrganizationId == o.Id).ToListAsync(ct);
        var people = await users.GetAsync(members.Select(m => m.UserId)
            .Concat(history.Where(h => h.ChangedBy is not null).Select(h => h.ChangedBy!.Value))
            .Append(o.CreatedBy)
            .Concat(o.VerifiedBy is { } v ? [v] : [])
            .Distinct().ToList(), ct);
        AdminPersonDto? Person(Guid? userId) =>
            userId is { } uid && people.TryGetValue(uid, out var p) ? new AdminPersonDto(p.Id, p.Email, p.DisplayName) : null;

        var facts = (await FactsAsync([o.Id], ct)).GetValueOrDefault(o.Id) ?? OrganizationFacts.Empty;
        var version = db.Entry(o).Property(x => x.Version).CurrentValue;
        var memberDtos = members
            .Where(m => people.ContainsKey(m.UserId))
            .Select(m => (m, u: people[m.UserId]))
            .OrderBy(x => x.m.Role).ThenBy(x => x.u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new AdminMemberDto(x.m.UserId, x.u.Email, x.u.DisplayName, x.m.Role.ToString(), x.m.CreatedAt, x.u.EmailVerified, !x.u.IsActive))
            .ToList();

        return new AdminOrganizationDetailDto(
            ToDto(o, members.Count, facts),
            version,
            Person(o.CreatedBy),
            Person(o.VerifiedBy),
            history.Select(h => new AdminStatusChangeDto(h.FromStatus?.ToString(), h.ToStatus.ToString(), h.Reason, h.ChangedAt, Person(h.ChangedBy))).ToList(),
            memberDtos,
            facts.RegisterNames,
            facts.Counts.GetValueOrDefault(OrganizationFactKeys.PublishedOffers));
    }

    private static AdminOrganizationDto ToDto(Organization o, int memberCount, OrganizationFacts facts) => new(
        o.Id, o.Name, o.LegalName, o.VatId, o.Street, o.PostalCode, o.City, o.CountryCode,
        o.IsWholesaler, o.IsRetailer, o.IsRepairProvider, o.Status.ToString(), o.StatusReason,
        o.CreatedAt, o.VerifiedAt, ModuleNames(o),
        o.TaxNumber, o.ContactEmail, o.ContactPhone, o.Website,
        memberCount,
        facts.Counts.GetValueOrDefault(OrganizationFactKeys.Registers),
        facts.Counts.GetValueOrDefault(OrganizationFactKeys.Offers));

    /// <summary>Platform actions run without a tenant; a tenant header on an admin call is a client error.</summary>
    private Error? RequireNoTenant() => tenant.OrganizationId is null
        ? null
        : Error.Validation("admin.tenant_header", "Platform administration is called without an organization header.");
}
