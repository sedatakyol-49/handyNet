namespace HandyNet.Modules.Pos.Contracts;

/// <summary>
/// Port for the POS fallback price: the current tenant's own marketplace offer price (net, cents) per product, preferring
/// an offer from <paramref name="warehouseId"/>. POS must not depend on Marketplace (ADR-0008, architecture tests), so the
/// host composes this from the Marketplace contract. The default implementation knows no offers.
/// </summary>
public interface IOfferPriceSource
{
    Task<IReadOnlyDictionary<Guid, long>> GetNetPricesAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}

internal sealed class NoOfferPrices : IOfferPriceSource
{
    public Task<IReadOnlyDictionary<Guid, long>> GetNetPricesAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, long>>(new Dictionary<Guid, long>());
}
