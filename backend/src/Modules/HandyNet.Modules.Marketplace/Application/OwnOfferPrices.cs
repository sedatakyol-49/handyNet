using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Marketplace.Application;

internal sealed class OwnOfferPrices(HandyNetDbContext db, ITenantContext tenant) : IOwnOfferPrices
{
    public async Task<IReadOnlyList<OwnOfferPrice>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0)
            return [];
        var organizationId = tenant.RequiredOrganizationId;
        return await db.Set<Offer>().AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && productIds.Contains(o.ProductId) && o.Status != OfferStatus.Archived)
            .Select(o => new OwnOfferPrice(o.Id, o.ProductId, o.WarehouseId, o.PriceMinor, o.Status, o.UpdatedAt))
            .ToListAsync(ct);
    }
}
