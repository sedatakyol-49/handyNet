using HandyNet.SharedKernel;

namespace HandyNet.Modules.Marketplace.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum OfferCondition
{
    New,
    Refurbished,
    Used,
}

public enum OfferStatus
{
    Draft,
    Published,
    Paused,
    Archived,
}

/// <summary>
/// Platform moderation state of an offer, separate from the seller's own <see cref="OfferStatus"/>. Stored as text.
/// Only the platform changes it (database function <c>marketplace.moderate_offers</c>; a trigger rejects any other change).
/// </summary>
public enum ModerationStatus
{
    None,
    Blocked,
}

public sealed record OfferPriceChanged(Guid OfferId, Guid OrganizationId, long OldPriceMinor, long NewPriceMinor, string Currency) : IDomainEvent;

/// <summary>
/// A seller's commercial terms for a canonical product: their SKU, net price, condition and delivery
/// time. Several sellers can offer the same product. Stock is not stored here — the offer points at
/// the seller's own warehouse, and availability comes from Inventory.
/// B2B offers are only visible to authenticated, verified business tenants.
/// </summary>
public sealed class Offer : Entity, IAuditable
{
    public const int MaxDeliveryDays = 60;

    private Offer() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string SupplierSku { get; private set; } = null!;
    public string SkuKey { get; private set; } = null!;
    public long PriceMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public int MinOrderQuantity { get; private set; }
    public int DeliveryDays { get; private set; }
    public OfferCondition Condition { get; private set; }
    public OfferStatus Status { get; private set; }

    /// <summary>Platform moderation; never changed by the seller (see <see cref="ModerationStatus"/>).</summary>
    public ModerationStatus Moderation { get; private set; }

    /// <summary>Reason given by the platform for the current block (null when not blocked).</summary>
    public string? ModerationReason { get; private set; }

    /// <summary>When the platform last blocked or unblocked the offer.</summary>
    public DateTimeOffset? ModeratedAt { get; private set; }

    /// <summary>Platform admin (user id) who last blocked or unblocked the offer.</summary>
    public Guid? ModeratedBy { get; private set; }

    public bool IsBlocked => Moderation == ModerationStatus.Blocked;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<Offer> Create(Guid organizationId, Guid productId, Guid warehouseId, string supplierSku,
        OfferTerms terms, bool publish, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(supplierSku))
            return Error.Validation("offer.sku_required", "Supplier SKU is required.");
        if (terms.Validate() is { } invalid)
            return invalid;

        var offer = new Offer
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ProductId = productId,
            WarehouseId = warehouseId,
            SupplierSku = supplierSku.Trim(),
            SkuKey = Text.NormalizeKey(supplierSku),
            Status = publish ? OfferStatus.Published : OfferStatus.Draft,
            CreatedAt = now,
        };
        offer.ApplyTerms(terms, now);
        return offer;
    }

    public Result UpdateTerms(OfferTerms terms, DateTimeOffset now)
    {
        if (Status == OfferStatus.Archived)
            return Error.Conflict("offer.archived", "Archived offers cannot be changed.");
        if (terms.Validate() is { } invalid)
            return invalid;
        if (terms.Price.MinorUnits != PriceMinor)
            Raise(new OfferPriceChanged(Id, OrganizationId, PriceMinor, terms.Price.MinorUnits, terms.Price.Currency));
        ApplyTerms(terms, now);
        return Result.Success();
    }

    private void ApplyTerms(OfferTerms terms, DateTimeOffset now)
    {
        PriceMinor = terms.Price.MinorUnits;
        Currency = terms.Price.Currency;
        MinOrderQuantity = terms.MinOrderQuantity;
        DeliveryDays = terms.DeliveryDays;
        Condition = terms.Condition;
        UpdatedAt = now;
    }

    public Result ChangeStatus(OfferStatus target, DateTimeOffset now)
    {
        if (Status == OfferStatus.Archived)
            return Error.Conflict("offer.archived", "Archived offers cannot be changed.");
        if (target == OfferStatus.Draft)
            return Error.Validation("offer.invalid_status", "An offer cannot return to draft.");
        if (IsBlocked)
            return Error.Conflict("offer.blocked", "The platform has blocked this offer; its status cannot be changed until the block is lifted.");
        Status = target;
        UpdatedAt = now;
        return Result.Success();
    }
}

/// <summary>Rules for platform moderation actions (blocking/unblocking offers and sellers).</summary>
public static class OfferModeration
{
    public const int MaxReasonLength = 500;

    /// <summary>Most offers one bulk action may name; larger sets are blocked per seller.</summary>
    public const int MaxBatchSize = 500;

    /// <summary>Trimmed reason, null when empty; rejects reasons over <see cref="MaxReasonLength"/> characters.</summary>
    public static Result<string?> NormalizeReason(string? reason)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > MaxReasonLength })
            return Error.Validation("moderation.reason_too_long", $"The reason may have at most {MaxReasonLength} characters.");
        return Result<string?>.Success(trimmed);
    }

    /// <summary>Distinct, non-empty offer ids of one bulk action (1 to <see cref="MaxBatchSize"/>).</summary>
    public static Result<IReadOnlyList<Guid>> NormalizeBatch(IEnumerable<Guid>? offerIds)
    {
        var ids = (offerIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return Error.Validation("moderation.no_offers", "Select at least one offer.");
        if (ids.Count > MaxBatchSize)
            return Error.Validation("moderation.too_many_offers", $"At most {MaxBatchSize} offers per action.");
        return ids;
    }
}

/// <summary>
/// Platform block of a seller: while active, none of the seller's offers — existing or created later — appear on the
/// marketplace or can be ordered. Read-only for the application; written only by <c>marketplace.moderate_seller</c>.
/// Lifting the block keeps the row (with <see cref="IsBlocked"/> false) so the last decision stays visible.
/// </summary>
public sealed class SellerBlock
{
    private SellerBlock() { }

    public Guid OrganizationId { get; private set; }
    public bool IsBlocked { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }
    public Guid ChangedBy { get; private set; }
}

public sealed record OfferTerms(Money Price, int MinOrderQuantity, int DeliveryDays, OfferCondition Condition)
{
    public Error? Validate()
    {
        if (Price.Currency != Money.Eur)
            return Error.Validation("offer.currency", "Only EUR prices are supported.");
        if (Price.MinorUnits <= 0)
            return Error.Validation("offer.price", "Price must be greater than zero.");
        if (MinOrderQuantity < 1)
            return Error.Validation("offer.min_quantity", "Minimum order quantity must be at least 1.");
        if (DeliveryDays is < 0 or > Offer.MaxDeliveryDays)
            return Error.Validation("offer.delivery_days", $"Delivery time must be between 0 and {Offer.MaxDeliveryDays} days.");
        return null;
    }
}

/// <summary>Read model over the database view <c>marketplace.offer_listings</c> (published offers of verified sellers).</summary>
public sealed class OfferListing
{
    public Guid OfferId { get; private set; }
    public Guid SellerOrganizationId { get; private set; }
    public string SellerName { get; private set; } = null!;
    public string SellerCity { get; private set; } = null!;
    public Guid ProductId { get; private set; }
    public string Brand { get; private set; } = null!;
    public string ProductName { get; private set; } = null!;
    public string Category { get; private set; } = null!;
    public string Quality { get; private set; } = null!;
    public string? Ean { get; private set; }
    public string? Mpn { get; private set; }
    public string SupplierSku { get; private set; } = null!;
    public long PriceMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public int MinOrderQuantity { get; private set; }
    public int DeliveryDays { get; private set; }
    public string Condition { get; private set; } = null!;
    public int Available { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? ModelName { get; private set; }
    public List<string> CompatibleModels { get; private set; } = [];

    /// <summary>Normalized model name + compatible models (catalog <c>model_keys</c>) for "passend für".</summary>
    public List<string> ModelKeys { get; private set; } = [];
    public int ImageCount { get; private set; }
    public Guid? PrimaryImageId { get; private set; }
}
