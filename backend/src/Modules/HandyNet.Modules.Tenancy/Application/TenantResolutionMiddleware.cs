using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Tenancy.Application;

/// <summary>
/// Resolves the organization a request acts for. The client names it in the X-Organization-Id header,
/// but the value is only accepted after the authenticated user's membership is confirmed in the
/// database. Unknown or foreign organizations leave the tenant context empty, and every
/// tenant-scoped endpoint then answers 403. Must run before anything touches tenant data, because
/// the PostgreSQL session variable used by Row-Level Security is derived from this context.
/// </summary>
internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, TenantContext tenant, HandyNetDbContext db)
    {
        if (currentUser.IsAuthenticated
            && context.Request.Headers.TryGetValue(HandyNetClaims.OrganizationHeader, out var header)
            && Guid.TryParse(header.ToString(), out var organizationId))
        {
            var userId = currentUser.UserId;
            var membership = await db.Set<Membership>().AsNoTracking()
                .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
                .Select(m => new { m.Role })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (membership is not null)
            {
                var organization = await db.Set<Organization>().AsNoTracking()
                    .Include(o => o.Modules)
                    .FirstAsync(o => o.Id == organizationId, context.RequestAborted);

                tenant.Set(
                    organizationId,
                    organization.Status == OrganizationStatus.Verified,
                    membership.Role,
                    organization.Modules.Select(m => m.Module).ToHashSet());
            }
        }

        await next(context);
    }
}
