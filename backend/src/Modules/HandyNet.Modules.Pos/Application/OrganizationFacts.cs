using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// Register names and counts for the platform's company administration. Reads through <c>pos.admin_register_names</c>
/// (SECURITY DEFINER, refuses tenant sessions): only id, name and active flag of registers — no receipts, sessions or sales.
/// </summary>
internal sealed class PosOrganizationFacts(HandyNetDbContext db) : IOrganizationFactSource
{
    public async Task<IReadOnlyDictionary<Guid, OrganizationFacts>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct)
    {
        var ids = organizationIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<Guid, OrganizationFacts>();
        var rows = await db.Database.SqlQuery<RegisterNameRow>($"""
                SELECT organization_id, name, is_active FROM pos.admin_register_names({ids}::uuid[])
                """)
            .ToListAsync(ct);
        return rows.GroupBy(r => r.OrganizationId).ToDictionary(
            g => g.Key,
            g => new OrganizationFacts(
                new Dictionary<string, int> { [OrganizationFactKeys.Registers] = g.Count() },
                g.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => r.Name).ToList()));
    }

    internal sealed record RegisterNameRow(Guid OrganizationId, string Name, bool IsActive);
}
