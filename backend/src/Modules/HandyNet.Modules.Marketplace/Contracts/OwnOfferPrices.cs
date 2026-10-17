using HandyNet.Modules.Marketplace.Domain;

namespace HandyNet.Modules.Marketplace.Contracts;

/// <summary>A (non-archived) offer of the current tenant: its net B2B price for a product.</summary>
public sealed record OwnOfferPrice(Guid OfferId, Guid ProductId, Guid WarehouseId, long NetPriceMinor, OfferStatus Status, DateTimeOffset UpdatedAt);

/// <summary>The current tenant's own offer prices (e.g. as a fallback sales price at the till). Never returns other tenants' offers.</summary>
public interface IOwnOfferPrices
{
    Task<IReadOnlyList<OwnOfferPrice>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}
