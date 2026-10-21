using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Tenancy.Application;

/// <summary>Memberships for the platform administration of user accounts (Identity's port; memberships are owned here).</summary>
internal sealed class UserMembershipSource(HandyNetDbContext db) : IUserMembershipSource
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<UserMembershipInfo>>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<UserMembershipInfo>>();
        var rows = await (
                from m in db.Set<Membership>().AsNoTracking()
                join o in db.Set<Organization>().AsNoTracking() on m.OrganizationId equals o.Id
                where userIds.Contains(m.UserId)
                select new { m.UserId, m.OrganizationId, o.Name, m.Role, o.Status, m.CreatedAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.UserId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<UserMembershipInfo>)g.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => new UserMembershipInfo(r.OrganizationId, r.Name, r.Role.ToString(), r.Status.ToString(), r.CreatedAt))
                .ToList());
    }

    public IQueryable<Guid> UserIdsWithMembership() => db.Set<Membership>().Select(m => m.UserId);
}
