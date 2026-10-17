using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Modules.Pos.Contracts;

namespace HandyNet.Api.Composition;

/// <summary>
/// Composes the POS fallback price from the Marketplace contract (POS itself must not depend on Marketplace). Per product:
/// an offer from the register's warehouse first, then published before paused/draft, then the most recently updated.
/// </summary>
internal sealed class PosOfferPriceSource(IOwnOfferPrices offers) : IOfferPriceSource
{
    public async Task<IReadOnlyDictionary<Guid, long>> GetNetPricesAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        (await offers.GetAsync(productIds, ct))
        .GroupBy(o => o.ProductId)
        .ToDictionary(g => g.Key, g => g
            .OrderByDescending(o => o.WarehouseId == warehouseId)
            .ThenByDescending(o => o.Status == OfferStatus.Published)
            .ThenByDescending(o => o.UpdatedAt)
            .First().NetPriceMinor);
}
