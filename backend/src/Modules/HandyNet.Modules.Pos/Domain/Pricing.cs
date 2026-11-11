using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>Where the till's price of a product comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PriceSource>))]
public enum PriceSource
{
    /// <summary>No price known: the cashier enters it (no price-override permission needed).</summary>
    None,

    /// <summary>The organization's own POS sales price (gross).</summary>
    PosPrice,

    /// <summary>Derived from the organization's own marketplace offer (net B2B price + 19 % VAT).</summary>
    Offer,

    /// <summary>From the customer's price list (ADR-0020): its item price, or the base price less its general discount.</summary>
    Pricelist,
}

public sealed record EffectivePrice(long? GrossMinor, PriceSource Source)
{
    public static readonly EffectivePrice None = new(null, PriceSource.None);
}

/// <summary>
/// The organization's own gross POS sales price for a catalog product (one row per organization and product).
/// <see cref="PriceMinor"/> null = no own POS price (the till falls back to the own offer price, if any). Changes are
/// recorded in the audit log; rows are never deleted.
/// </summary>
public sealed class PosPrice : IAuditable
{
    private PosPrice() { }

    public Guid OrganizationId { get; private set; }
    public Guid ProductId { get; private set; }
    public long? PriceMinor { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public uint Version { get; private set; }

    public static Result<PosPrice> Create(Guid organizationId, Guid productId, long? priceMinor, Guid userId, DateTimeOffset now)
    {
        var price = new PosPrice { OrganizationId = organizationId, ProductId = productId };
        var set = price.Change(priceMinor, userId, now);
        return set.IsSuccess ? price : set.Error!;
    }

    public Result Change(long? priceMinor, Guid userId, DateTimeOffset now)
    {
        if (priceMinor is not null && priceMinor is <= 0 or > PosPricing.MaxPriceMinor)
            return Error.Validation("pos.price", $"The price must be between 0.01 and {PosPricing.MaxPriceMinor / 100:N0} €.");
        PriceMinor = priceMinor;
        UpdatedAt = now;
        UpdatedBy = userId;
        return Result.Success();
    }
}

public static class PosPricing
{
    /// <summary>Plausibility cap (1 million euro), same as other POS amounts.</summary>
    public const long MaxPriceMinor = 100_000_000;

    /// <summary>
    /// Gross consumer price from a net B2B offer price: net + 19 % VAT, rounded half away from zero (ADR-0004 rounding).
    /// </summary>
    public static long GrossFromNet(long netMinor) => checked(netMinor + new Money(netMinor, Money.Eur).ApplyRate(VatCalculator.StandardRateBasisPoints).MinorUnits);

    /// <summary>Own POS price first; otherwise the own offer's net price converted to gross; otherwise none.</summary>
    public static EffectivePrice Resolve(long? posPriceMinor, long? offerNetPriceMinor) =>
        posPriceMinor is > 0 ? new EffectivePrice(posPriceMinor, PriceSource.PosPrice)
        : offerNetPriceMinor is > 0 ? new EffectivePrice(GrossFromNet(offerNetPriceMinor.Value), PriceSource.Offer)
        : EffectivePrice.None;
}

/// <summary>What the cashier may do on a sale line (from the membership's permissions).</summary>
public sealed record CashierRights(bool MayOverridePrice, bool MayDiscount, int MaxDiscountPercent)
{
    public static readonly CashierRights Unrestricted = new(true, true, 100);
}

/// <summary>Per-cashier rules for sale lines (Odoo-style price change and discount limits).</summary>
public static class LinePolicy
{
    /// <summary>
    /// A line priced differently from the product's known price needs the price-override right; any discount needs the
    /// discount right and may not exceed the cashier's limit (percent of unit price × quantity). Without a known price the
    /// cashier enters it freely. Errors are 403 with a stable code.
    /// </summary>
    public static Result Check(int lineNumber, long unitPriceMinor, int quantity, long discountMinor, EffectivePrice price, CashierRights rights)
    {
        if (price.GrossMinor is { } expected && unitPriceMinor != expected && !rights.MayOverridePrice)
            return Error.Forbidden("pos.price_override_forbidden",
                    $"Line {lineNumber}: the price is {expected / 100m:0.00} €; changing it needs the permission 'pos.price_override'.")
                .WithDetails(new Dictionary<string, object?>
                {
                    ["lineNumber"] = lineNumber, ["expectedUnitPriceMinor"] = expected, ["priceSource"] = price.Source.ToString(),
                });
        if (discountMinor <= 0)
            return Result.Success();
        if (!rights.MayDiscount)
            return Error.Forbidden("pos.discount_forbidden", $"Line {lineNumber}: giving a discount needs the permission 'pos.discount'.")
                .WithDetails(new Dictionary<string, object?> { ["lineNumber"] = lineNumber });
        var lineAmount = checked(unitPriceMinor * quantity);
        // discount / amount > max% ⇔ discount × 100 > max × amount (integers, no rounding).
        if (checked(discountMinor * 100) > checked((long)rights.MaxDiscountPercent * lineAmount))
            return Error.Forbidden("pos.discount_limit_exceeded",
                    $"Line {lineNumber}: at most {rights.MaxDiscountPercent} % discount is allowed for you.")
                .WithDetails(new Dictionary<string, object?>
                {
                    ["lineNumber"] = lineNumber, ["maxDiscountPercent"] = rights.MaxDiscountPercent,
                    ["maxDiscountMinor"] = rights.MaxDiscountPercent * lineAmount / 100,
                });
        return Result.Success();
    }
}
