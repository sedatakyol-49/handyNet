using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Tenancy.Application;

internal sealed class OrganizationModuleDirectory(HandyNetDbContext db) : IOrganizationModuleDirectory
{
    public async Task<IReadOnlyList<OrganizationSummary>> ListWithModuleAsync(PlatformModule module, string? search, int limit, CancellationToken ct)
    {
        var query = db.Set<Organization>().AsNoTracking().Where(o => o.Modules.Any(m => m.Module == module));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Text.LikePattern(search);
            query = query.Where(o => EF.Functions.ILike(o.Name, pattern) || EF.Functions.ILike(o.City, pattern));
        }
        return await query.OrderBy(o => o.Name).Take(Math.Clamp(limit, 1, 500))
            .Select(o => new OrganizationSummary(o.Id, o.Name, o.City, o.Status == OrganizationStatus.Verified))
            .ToListAsync(ct);
    }
}
