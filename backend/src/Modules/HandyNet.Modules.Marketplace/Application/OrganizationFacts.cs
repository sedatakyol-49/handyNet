using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Marketplace.Application;

/// <summary>
/// Offer counts for the platform's company administration, through <c>marketplace.admin_offer_counts</c> (SECURITY DEFINER,
/// refuses tenant sessions): counts only — no prices, products or stock.
/// </summary>
internal sealed class MarketplaceOrganizationFacts(HandyNetDbContext db) : IOrganizationFactSource
{
    public async Task<IReadOnlyDictionary<Guid, OrganizationFacts>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct)
    {
        var ids = organizationIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<Guid, OrganizationFacts>();
        var rows = await db.Database.SqlQuery<OfferCountRow>($"""
                SELECT organization_id, offer_count, published_count FROM marketplace.admin_offer_counts({ids}::uuid[])
                """)
            .ToListAsync(ct);
        return rows.ToDictionary(
            r => r.OrganizationId,
            r => new OrganizationFacts(
                new Dictionary<string, int>
                {
                    [OrganizationFactKeys.Offers] = (int)r.OfferCount,
                    [OrganizationFactKeys.PublishedOffers] = (int)r.PublishedCount,
                },
                []));
    }

    internal sealed record OfferCountRow(Guid OrganizationId, long OfferCount, long PublishedCount);
}
