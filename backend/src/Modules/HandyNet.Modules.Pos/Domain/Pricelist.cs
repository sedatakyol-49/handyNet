using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// A price list (Preisliste, ADR-0020) for customers of the store: a general discount off the till price and/or fixed gross
/// prices for single products (<see cref="PricelistItem"/>). A customer has at most one price list; it applies while it is
/// active and inside its validity (dates in Europe/Berlin, both inclusive). Configuration (audited); price lists are never
/// deleted because customers and receipts reference them — deactivate instead.
/// </summary>
public sealed class Pricelist : IAuditable
{
    public const int MaxNameLength = 100;
    public const int MaxDiscountBasisPoints = 10_000;
    public const int MaxItems = 5_000;

    private Pricelist() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>General discount off the till price in basis points (0–10000 = 0–100 %); 0 = only item prices apply.</summary>
    public int DiscountBasisPoints { get; private set; }
    public bool IsActive { get; private set; }
    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<Pricelist> Create(Guid organizationId, string name, int discountBasisPoints, bool isActive, DateOnly? validFrom, DateOnly? validTo,
        DateTimeOffset now)
    {
        var pricelist = new Pricelist { Id = Guid.CreateVersion7(), OrganizationId = organizationId, CreatedAt = now };
        var applied = pricelist.Update(name, discountBasisPoints, isActive, validFrom, validTo, now);
        return applied.IsSuccess ? pricelist : applied.Error!;
    }

    public Result Update(string name, int discountBasisPoints, bool isActive, DateOnly? validFrom, DateOnly? validTo, DateTimeOffset now)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
            return Error.Validation("pos.pricelist_name", $"A price list name (max {MaxNameLength} characters) is required.");
        if (discountBasisPoints is < 0 or > MaxDiscountBasisPoints)
            return Error.Validation("pos.pricelist_discount", "The discount must be between 0 and 10000 basis points (0–100 %).");
        if (validFrom is { } from && validTo is { } to && from > to)
            return Error.Validation("pos.pricelist_validity", "'validFrom' must not be after 'validTo'.");
        Name = trimmed;
        DiscountBasisPoints = discountBasisPoints;
        IsActive = isActive;
        ValidFrom = validFrom;
        ValidTo = validTo;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Active and inside its validity on that day (both ends inclusive).</summary>
    public bool AppliesOn(DateOnly day) => IsActive && (ValidFrom is null || ValidFrom <= day) && (ValidTo is null || day <= ValidTo);

    /// <summary>
    /// The till price for a customer of this price list: the item price if the list has one for the product; otherwise the
    /// base till price less the general discount (rounded half away from zero, never below one cent); otherwise the base.
    /// </summary>
    public EffectivePrice Resolve(EffectivePrice basePrice, long? itemPriceMinor)
    {
        if (itemPriceMinor is > 0)
            return new EffectivePrice(itemPriceMinor, PriceSource.Pricelist);
        if (DiscountBasisPoints > 0 && basePrice.GrossMinor is { } gross)
            return new EffectivePrice(Discounted(gross, DiscountBasisPoints), PriceSource.Pricelist);
        return basePrice;
    }

    /// <summary>base − round_half_away(base × bp / 10000); a line needs a positive price, so the result is at least one cent.</summary>
    public static long Discounted(long baseMinor, int discountBasisPoints)
    {
        var discount = (long)decimal.Round(baseMinor * (decimal)discountBasisPoints / 10_000m, 0, MidpointRounding.AwayFromZero);
        return Math.Max(1, baseMinor - discount);
    }
}

/// <summary>A fixed gross price of one product on a price list. Configuration: may be changed and deleted (audited).</summary>
public sealed class PricelistItem : IAuditable
{
    private PricelistItem() { }

    public Guid OrganizationId { get; private set; }
    public Guid PricelistId { get; private set; }
    public Guid ProductId { get; private set; }
    public long PriceMinor { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static Result<PricelistItem> Create(Pricelist pricelist, Guid productId, long priceMinor, Guid userId, DateTimeOffset now)
    {
        var item = new PricelistItem { OrganizationId = pricelist.OrganizationId, PricelistId = pricelist.Id, ProductId = productId };
        var set = item.Change(priceMinor, userId, now);
        return set.IsSuccess ? item : set.Error!;
    }

    public Result Change(long priceMinor, Guid userId, DateTimeOffset now)
    {
        if (priceMinor is <= 0 or > PosPricing.MaxPriceMinor)
            return Error.Validation("pos.price", $"The price must be between 0.01 and {PosPricing.MaxPriceMinor / 100:N0} €.");
        PriceMinor = priceMinor;
        UpdatedAt = now;
        UpdatedBy = userId;
        return Result.Success();
    }
}
