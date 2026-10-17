using HandyNet.Modules.Catalog.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Imports.Domain;

public enum EntryProductAction
{
    /// <summary>The client picked an existing product (productId).</summary>
    Selected,

    /// <summary>New product data matched an existing canonical product (same conservative matcher as the import).</summary>
    MatchedExisting,

    Created,
}

public enum EntryOfferAction
{
    None,
    Created,
    Updated,
}

/// <summary>
/// One manual Lager entry ("Artikel anlegen"): the append-only record of what was applied, keyed by the client's
/// Idempotency-Key so a retried request returns the original result instead of receiving stock twice.
/// </summary>
public sealed class ManualEntry
{
    private ManualEntry() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public Guid ProductId { get; private set; }
    public EntryProductAction ProductAction { get; private set; }
    public Guid WarehouseId { get; private set; }
    public int QuantityReceived { get; private set; }
    public List<Guid> UnitIds { get; private set; } = [];
    public long? UnitCostMinor { get; private set; }
    public Guid? OfferId { get; private set; }
    public EntryOfferAction OfferAction { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The response as first returned (JSON), replayed verbatim for a retried Idempotency-Key.</summary>
    public string Result { get; private set; } = "{}";

    /// <summary>Set once before the row is inserted; the table is insert-only for the application role.</summary>
    public void SetResult(string json) => Result = json;

    public static ManualEntry Record(Guid id, Guid organizationId, string idempotencyKey, string requestHash, Guid productId,
        EntryProductAction productAction, Guid warehouseId, int quantityReceived, IReadOnlyList<Guid> unitIds, long? unitCostMinor,
        Guid? offerId, EntryOfferAction offerAction, Guid userId, DateTimeOffset now) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        IdempotencyKey = idempotencyKey,
        RequestHash = requestHash,
        ProductId = productId,
        ProductAction = productAction,
        WarehouseId = warehouseId,
        QuantityReceived = quantityReceived,
        UnitIds = unitIds.ToList(),
        UnitCostMinor = unitCostMinor,
        OfferId = offerId,
        OfferAction = offerAction,
        CreatedBy = userId,
        CreatedAt = now,
    };
}

/// <summary>Business rules of a manual entry that do not need the database.</summary>
public static class ManualEntryRules
{
    public const int MaxImeis = 500;
    public const int MaxQuantity = 1_000_000;

    /// <summary>
    /// Serialized categories: smartphones are stocked only by IMEI (one per unit); tablets/wearables may be; parts and
    /// accessories never. B2B offers for serialized stock are not supported yet (a B2B shipment would decrease the stock
    /// level without marking a specific IMEI unit sold), so an offer cannot be combined with IMEI stock.
    /// </summary>
    public static Error? CheckStock(ProductCategory category, int? quantity, int imeiCount, bool withOffer, bool productHasSerializedStock)
    {
        if (imeiCount > 0 && !SerializedStock.AllowsImei(category))
            return Error.Validation("entry.imei_not_allowed", $"Products of category {category} are not stocked by IMEI. Enter a quantity.");
        if (imeiCount == 0 && quantity is > 0 && (SerializedStock.RequiresImei(category) || productHasSerializedStock))
            return Error.Validation("entry.imei_required", "This product is stocked as serialized devices: enter one IMEI per unit.");
        if (imeiCount > 0 && quantity is { } q && q != imeiCount)
            return Error.Validation("entry.quantity_mismatch", $"Quantity {q} does not match the {imeiCount} IMEIs entered.");
        if (withOffer && (imeiCount > 0 || productHasSerializedStock))
            return Error.Validation("entry.serialized_offer_unsupported",
                "B2B offers for serialized (IMEI) stock are not supported yet. Save the stock without an offer.");
        return null;
    }
}
