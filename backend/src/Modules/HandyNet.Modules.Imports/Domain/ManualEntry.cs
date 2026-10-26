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

    /// <summary>
    /// Refusal listing every IMEI that cannot be stocked, in entry order: <c>imei.blacklisted</c> (reported lost/stolen/blocked,
    /// takes precedence) or <c>unit.imei_in_stock</c> (already in stock anywhere on the platform). Details: <c>imeis</c> =
    /// [{ imei, code }]. With a single offending IMEI the error code is that IMEI's code; with several it is
    /// <c>entry.imei_conflicts</c>.
    /// </summary>
    public static Error ImeiConflicts(IEnumerable<string> imeis, IReadOnlySet<string> blacklisted, IReadOnlySet<string> inStock)
    {
        var problems = imeis
            .Where(i => blacklisted.Contains(i) || inStock.Contains(i))
            .Distinct(StringComparer.Ordinal)
            .Select(i => new ImeiProblem(i, blacklisted.Contains(i) ? "imei.blacklisted" : "unit.imei_in_stock"))
            .ToList();
        var details = new Dictionary<string, object?> { ["imeis"] = problems };
        if (problems.Count == 1)
        {
            var only = problems[0];
            details["imei"] = only.Imei;
            return only.Code == "imei.blacklisted"
                ? Error.Conflict(only.Code, $"The device with IMEI {only.Imei} is reported as lost, stolen or blocked. Do not stock it; contact the platform.").WithDetails(details)
                : Error.Conflict(only.Code, $"IMEI {only.Imei} is already in stock. The same device cannot be stocked twice.").WithDetails(details);
        }
        return Error.Conflict("entry.imei_conflicts",
            $"{problems.Count} devices cannot be stocked (reported as lost/stolen/blocked or already in stock). Nothing was saved.").WithDetails(details);
    }
}

/// <summary>One IMEI that prevents an entry, with a stable code (<c>imei.blacklisted</c>, <c>unit.imei_in_stock</c>).</summary>
public sealed record ImeiProblem(string Imei, string Code);
