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
        Status = target;
        UpdatedAt = now;
        return Result.Success();
    }
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
}
