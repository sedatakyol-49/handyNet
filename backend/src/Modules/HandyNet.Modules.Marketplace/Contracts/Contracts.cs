using HandyNet.Modules.Marketplace.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Marketplace.Contracts;

public sealed record ListingDto(
    Guid OfferId,
    Guid SellerOrganizationId,
    string SellerName,
    string SellerCity,
    Guid ProductId,
    string Brand,
    string ProductName,
    string Category,
    string Quality,
    string? Ean,
    string? Mpn,
    string SupplierSku,
    long PriceMinor,
    string Currency,
    int MinOrderQuantity,
    int DeliveryDays,
    string Condition,
    int Available,
    DateTimeOffset UpdatedAt);

/// <summary>Used by Ordering to price and reserve published offers.</summary>
public interface IOfferReservations
{
    Task<IReadOnlyDictionary<Guid, ListingDto>> GetListingsAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct);

    /// <summary>
    /// Atomically reserves seller stock for a published offer on behalf of the current tenant.
    /// Returns null when the offer is not orderable or available stock is insufficient.
    /// </summary>
    Task<Guid?> ReserveAsync(Guid offerId, int quantity, string reference, CancellationToken ct);
}

public sealed record ExistingOffer(
    Guid Id, string SkuKey, Guid ProductId, long PriceMinor, int MinOrderQuantity, int DeliveryDays, OfferCondition Condition, OfferStatus Status);

public sealed record OfferUpsert(string SupplierSku, Guid ProductId, Guid WarehouseId, OfferTerms Terms, bool Publish);

/// <summary>Used by Imports to synchronize the current tenant's offers.</summary>
public interface IOfferSync
{
    Task<IReadOnlyDictionary<string, ExistingOffer>> GetBySkuKeysAsync(IReadOnlyCollection<string> skuKeys, CancellationToken ct);

    /// <summary>Stages create-or-update of an offer identified by the tenant's SKU; the caller commits.</summary>
    Task<Result<Guid>> StageUpsertAsync(OfferUpsert upsert, CancellationToken ct);
}
