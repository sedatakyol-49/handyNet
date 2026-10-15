using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Tenancy.Application;

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
    string[] Modules);

public sealed record ReasonRequest(string Reason);

public sealed record SetModulesRequest(string[] Modules);

internal sealed class AdministrationService(HandyNetDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<PagedResult<AdminOrganizationDto>> ListAsync(string? status, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Organization>().AsNoTracking().Include(o => o.Modules).AsQueryable();

        if (Enum.TryParse<OrganizationStatus>(status, true, out var parsed))
            query = query.Where(o => o.Status == parsed);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Text.LikePattern(search);
            query = query.Where(o => EF.Functions.ILike(o.Name, pattern) || EF.Functions.ILike(o.LegalName, pattern)
                                     || (o.VatId != null && EF.Functions.ILike(o.VatId, pattern)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(o => o.Status).ThenByDescending(o => o.CreatedAt)
            .Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AdminOrganizationDto>(items.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<Result<AdminOrganizationDto>> VerifyAsync(Guid id, CancellationToken ct) =>
        await MutateAsync(id, o => o.Verify(currentUser.UserId, clock.UtcNow), ct);

    public async Task<Result<AdminOrganizationDto>> RejectAsync(Guid id, string reason, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reason)
            ? Error.Validation("reason.required", "A reason is required.")
            : await MutateAsync(id, o => o.Reject(reason), ct);

    public async Task<Result<AdminOrganizationDto>> SuspendAsync(Guid id, string reason, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reason)
            ? Error.Validation("reason.required", "A reason is required.")
            : await MutateAsync(id, o => o.Suspend(reason), ct);

    public async Task<Result<AdminOrganizationDto>> SetModulesAsync(Guid id, string[] modules, CancellationToken ct)
    {
        var parsed = new HashSet<PlatformModule>();
        foreach (var name in modules)
        {
            if (!Enum.TryParse<PlatformModule>(name, true, out var module) || !Enum.IsDefined(module))
                return Error.Validation("module.unknown", $"Unknown module '{name}'.");
            parsed.Add(module);
        }

        return await MutateAsync(id, o =>
        {
            o.SetModules(parsed, currentUser.UserId, clock.UtcNow);
            return Result.Success();
        }, ct);
    }

    private async Task<Result<AdminOrganizationDto>> MutateAsync(Guid id, Func<Organization, Result> action, CancellationToken ct)
    {
        var organization = await db.Set<Organization>().Include(o => o.Modules).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (organization is null)
            return Error.NotFound("organization.not_found", "Organization not found.");

        var result = action(organization);
        if (!result.IsSuccess)
            return result.Error!;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The organization was changed by someone else. Reload and try again.");
        }

        return ToDto(organization);
    }

    private static AdminOrganizationDto ToDto(Organization o) => new(
        o.Id, o.Name, o.LegalName, o.VatId, o.Street, o.PostalCode, o.City, o.CountryCode,
        o.IsWholesaler, o.IsRetailer, o.IsRepairProvider, o.Status.ToString(), o.StatusReason,
        o.CreatedAt, o.VerifiedAt, o.Modules.Select(m => m.Module.ToString()).Order().ToArray());
}
