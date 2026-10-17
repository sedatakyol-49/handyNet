using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Marketplace.Application;

/// <summary>
/// Marketplace filters. List parameters are repeated query parameters (<c>?brand=Apple&amp;brand=Samsung</c>) and match
/// exactly. Prices are net VK in EUR (e.g. <c>priceMin=10.5</c>); more than two decimals are rejected. "VKVK" from the
/// product owner is assumed to mean this VK range until confirmed.
/// </summary>
public sealed record BrowseQuery(
    string? Search,
    string[]? Category,
    string[]? Brand,
    Guid? ProductId,
    bool? InStockOnly,
    string? Sort,
    int? Page,
    int? PageSize,
    string[]? Quality,
    string[]? Condition,
    decimal? PriceMin,
    decimal? PriceMax,
    int? MaxDeliveryDays,
    Guid[]? SellerId,
    string? CompatibleModel,
    bool? HasImages);

internal sealed class BrowseQueryValidator : AbstractValidator<BrowseQuery>
{
    public BrowseQueryValidator()
    {
        RuleFor(x => x.PriceMin).GreaterThanOrEqualTo(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.PriceMax).GreaterThanOrEqualTo(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x).Must(x => x.PriceMin is null || x.PriceMax is null || x.PriceMin <= x.PriceMax)
            .WithName("PriceMax").WithMessage("priceMax must not be below priceMin.");
        RuleFor(x => x.MaxDeliveryDays).InclusiveBetween(0, Offer.MaxDeliveryDays);
        RuleFor(x => x.CompatibleModel).MaximumLength(200);
        RuleFor(x => x.Search).MaximumLength(200);
        RuleForEach(x => x.Brand).MaximumLength(100);
        RuleFor(x => x.Brand).Must(b => b is null || b.Length <= 50).WithMessage("At most 50 brands.");
        RuleFor(x => x.SellerId).Must(s => s is null || s.Length <= 50).WithMessage("At most 50 sellers.");
    }
}

public sealed record FacetValue(string Value, int Count);

public sealed record SellerFacet(Guid Id, string Name, string City, int Count);

public sealed record PriceRange(long MinMinor, long MaxMinor);

/// <summary>
/// Counts for the current filter set. Each list facet is counted with every filter applied except its own dimension
/// (disjunctive faceting), so a multi-select dropdown still shows the alternatives; the price range likewise ignores the
/// price filter. <see cref="Total"/> applies all filters.
/// </summary>
public sealed record FacetsDto(
    int Total,
    IReadOnlyList<FacetValue> Brands,
    IReadOnlyList<FacetValue> Categories,
    IReadOnlyList<FacetValue> Qualities,
    IReadOnlyList<FacetValue> Conditions,
    IReadOnlyList<SellerFacet> Sellers,
    PriceRange? Price,
    int WithImages);

public sealed record ProductDetailDto(
    Guid Id,
    string Brand,
    string Name,
    string Category,
    string Quality,
    string? Ean,
    string? Mpn,
    string? ModelName,
    IReadOnlyList<string> CompatibleModels,
    IReadOnlyList<ProductImageInfo> Images);

public sealed record OfferDetailDto(ListingDto Offer, ProductDetailDto Product, IReadOnlyList<ListingDto> OtherOffers);

internal sealed partial class OfferService
{
    public const int MaxOtherOffers = 50;

    private enum Dimension
    {
        None,
        Brand,
        Category,
        Quality,
        Condition,
        Seller,
        Price,
    }

    public async Task<IReadOnlyDictionary<Guid, ListingDto>> GetListingsAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct) =>
        (await db.Set<OfferListing>().AsNoTracking().Where(l => offerIds.Contains(l.OfferId)).ToListAsync(ct))
        .ToDictionary(l => l.OfferId, ToDto);

    public async Task<PagedResult<ListingDto>> BrowseAsync(BrowseQuery q, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = Filter(q, Dimension.None);

        var total = await query.CountAsync(ct);
        var ordered = q.Sort switch
        {
            "price_desc" => query.OrderByDescending(l => l.PriceMinor),
            "delivery" => query.OrderBy(l => l.DeliveryDays).ThenBy(l => l.PriceMinor),
            "name" => query.OrderBy(l => l.Brand).ThenBy(l => l.ProductName),
            _ => query.OrderBy(l => l.PriceMinor),
        };
        var items = await ordered.ThenBy(l => l.OfferId).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ListingDto>(items.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<FacetsDto> FacetsAsync(BrowseQuery q, CancellationToken ct)
    {
        var total = await Filter(q, Dimension.None).CountAsync(ct);
        var withImages = await Filter(q, Dimension.None).CountAsync(l => l.ImageCount > 0, ct);

        async Task<IReadOnlyList<FacetValue>> Count(Dimension dimension, System.Linq.Expressions.Expression<Func<OfferListing, string>> key) =>
            (await Filter(q, dimension).GroupBy(key).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new FacetValue(x.Key, x.Count)).ToList();

        var brands = await Count(Dimension.Brand, l => l.Brand);
        var categories = await Count(Dimension.Category, l => l.Category);
        var qualities = await Count(Dimension.Quality, l => l.Quality);
        var conditions = await Count(Dimension.Condition, l => l.Condition);
        var sellers = (await Filter(q, Dimension.Seller)
                .GroupBy(l => new { l.SellerOrganizationId, l.SellerName, l.SellerCity })
                .Select(g => new { g.Key.SellerOrganizationId, g.Key.SellerName, g.Key.SellerCity, Count = g.Count() })
                .ToListAsync(ct))
            .OrderByDescending(x => x.Count).ThenBy(x => x.SellerName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new SellerFacet(x.SellerOrganizationId, x.SellerName, x.SellerCity, x.Count)).ToList();

        var price = await Filter(q, Dimension.Price).GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(l => l.PriceMinor), Max = g.Max(l => l.PriceMinor) })
            .FirstOrDefaultAsync(ct);

        return new FacetsDto(total, brands, categories, qualities, conditions, sellers,
            price is null ? null : new PriceRange(price.Min, price.Max), withImages);
    }

    /// <summary>One published offer with all product data and images, plus the other published offers for the same product.</summary>
    public async Task<OfferDetailDto?> GetDetailAsync(Guid offerId, CancellationToken ct)
    {
        var listing = await db.Set<OfferListing>().AsNoTracking().FirstOrDefaultAsync(l => l.OfferId == offerId, ct);
        if (listing is null)
            return null;
        var others = await db.Set<OfferListing>().AsNoTracking()
            .Where(l => l.ProductId == listing.ProductId && l.OfferId != offerId)
            .OrderBy(l => l.PriceMinor).ThenBy(l => l.DeliveryDays).ThenBy(l => l.OfferId)
            .Take(MaxOtherOffers)
            .ToListAsync(ct);
        var images = (await catalog.GetImagesAsync([listing.ProductId], ct)).GetValueOrDefault(listing.ProductId) ?? [];
        var product = new ProductDetailDto(listing.ProductId, listing.Brand, listing.ProductName, listing.Category, listing.Quality,
            listing.Ean, listing.Mpn, listing.ModelName, listing.CompatibleModels, images);
        return new OfferDetailDto(ToDto(listing), product, others.Select(ToDto).ToList());
    }

    private IQueryable<OfferListing> Filter(BrowseQuery q, Dimension except)
    {
        var query = db.Set<OfferListing>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = Text.LikePattern(q.Search);
            var key = Text.NormalizeKey(q.Search);
            query = query.Where(l => EF.Functions.ILike(l.ProductName, pattern) || EF.Functions.ILike(l.Brand, pattern)
                                     || l.Ean == key || (l.Mpn != null && EF.Functions.ILike(l.Mpn, pattern))
                                     || EF.Functions.ILike(l.SupplierSku, pattern)
                                     || (l.ModelName != null && EF.Functions.ILike(l.ModelName, pattern)));
        }
        if (q.ProductId is { } productId)
            query = query.Where(l => l.ProductId == productId);
        if (q.InStockOnly == true)
            query = query.Where(l => l.Available > 0);
        if (q.MaxDeliveryDays is { } maxDays)
            query = query.Where(l => l.DeliveryDays <= maxDays);
        if (!string.IsNullOrWhiteSpace(q.CompatibleModel))
        {
            var modelKey = new[] { Text.NormalizeKey(q.CompatibleModel) };
            query = query.Where(l => modelKey.All(k => l.ModelKeys.Contains(k))); // model_keys @> ARRAY[key]
        }
        if (q.HasImages is { } hasImages)
            query = hasImages ? query.Where(l => l.ImageCount > 0) : query.Where(l => l.ImageCount == 0);

        if (except != Dimension.Brand && Values(q.Brand) is { } brands)
            query = query.Where(l => brands.Contains(l.Brand));
        if (except != Dimension.Category && Values(q.Category) is { } categories)
            query = query.Where(l => categories.Contains(l.Category));
        if (except != Dimension.Quality && Values(q.Quality) is { } qualities)
            query = query.Where(l => qualities.Contains(l.Quality));
        if (except != Dimension.Condition && Values(q.Condition) is { } conditions)
            query = query.Where(l => conditions.Contains(l.Condition));
        if (except != Dimension.Seller && q.SellerId is { Length: > 0 } sellers)
            query = query.Where(l => sellers.Contains(l.SellerOrganizationId));
        if (except != Dimension.Price)
        {
            if (q.PriceMin is { } min && Money.TryFromMajor(min, Money.Eur, out var minMoney))
                query = query.Where(l => l.PriceMinor >= minMoney.MinorUnits);
            if (q.PriceMax is { } max && Money.TryFromMajor(max, Money.Eur, out var maxMoney))
                query = query.Where(l => l.PriceMinor <= maxMoney.MinorUnits);
        }
        return query;
    }

    private static List<string>? Values(string[]? values)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static ListingDto ToDto(OfferListing l) => new(
        l.OfferId, l.SellerOrganizationId, l.SellerName, l.SellerCity, l.ProductId, l.Brand, l.ProductName, l.Category,
        l.Quality, l.Ean, l.Mpn, l.SupplierSku, l.PriceMinor, l.Currency, l.MinOrderQuantity, l.DeliveryDays, l.Condition,
        l.Available, l.UpdatedAt, l.ModelName, l.CompatibleModels, l.ImageCount,
        l.PrimaryImageId is { } image ? ProductImageUrls.Thumbnail(image) : null);
}
