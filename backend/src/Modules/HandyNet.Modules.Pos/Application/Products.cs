using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Pos.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// A product of the register's warehouse as the till shows it. Prices are gross cents. PriceMinor/PriceSource is the price
/// the till uses (own POS price, else own offer net price + 19 % VAT, else none = cashier enters it). ImageUrl is the
/// thumbnail of the catalog product's primary image (relative to the API base, public, immutable; null without images).
/// Serialized = sold by IMEI.
/// </summary>
public sealed record PosProductDto(
    Guid ProductId,
    string Brand,
    string Name,
    string Category,
    string Quality,
    string? Ean,
    string? Mpn,
    string? ImageUrl,
    bool Serialized,
    int OnHand,
    int Reserved,
    int Available,
    long? PriceMinor,
    string PriceSource,
    long? PosPriceMinor);

public sealed record PosCategoryDto(string Category, int ProductCount);

public sealed record SetPosPriceRequest(long? PriceMinor);

public sealed record PosPriceDto(Guid ProductId, long? PosPriceMinor, long? OfferNetPriceMinor, long? PriceMinor, string PriceSource, DateTimeOffset? UpdatedAt);

internal sealed class SetPosPriceValidator : AbstractValidator<SetPosPriceRequest>
{
    public SetPosPriceValidator() =>
        RuleFor(x => x.PriceMinor).InclusiveBetween(1, PosPricing.MaxPriceMinor).When(x => x.PriceMinor is not null);
}

/// <summary>Row of the read model <c>pos.sellable_stock</c> (view over inventory stock + catalog products + POS prices, RLS applies).</summary>
internal sealed class SellableStockRow
{
    public Guid OrganizationId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public string Brand { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Category { get; set; } = null!;
    public string Quality { get; set; } = null!;
    public string? Ean { get; set; }
    public string? Mpn { get; set; }
    public string? ModelName { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public int Available { get; set; }
    public bool Serialized { get; set; }
    public long? PosPriceMinor { get; set; }
}

/// <summary>Effective till prices: own POS price, else own offer (net → gross), else none.</summary>
internal sealed class PriceResolver(HandyNetDbContext db, ITenantContext tenant, IOfferPriceSource offers)
{
    public async Task<IReadOnlyDictionary<Guid, (EffectivePrice Price, long? PosPriceMinor, long? OfferNetMinor)>> ResolveAsync(Guid warehouseId,
        IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        var own = await db.Set<PosPrice>().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && productIds.Contains(p.ProductId) && p.PriceMinor != null)
            .ToDictionaryAsync(p => p.ProductId, p => p.PriceMinor, ct);
        var missing = productIds.Where(id => !own.ContainsKey(id)).ToList();
        var offer = missing.Count == 0 ? new Dictionary<Guid, long>() : await offers.GetNetPricesAsync(warehouseId, missing, ct);
        return productIds.Distinct().ToDictionary(id => id, id =>
        {
            var pos = own.GetValueOrDefault(id);
            long? net = offer.TryGetValue(id, out var n) ? n : null;
            return (PosPricing.Resolve(pos, net), pos, net);
        });
    }
}

internal sealed class ProductService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IProductCatalog catalog, PriceResolver prices,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    private IQueryable<SellableStockRow> Stock(Guid warehouseId, bool includeOutOfStock)
    {
        // Column names follow the snake_case convention EF applies to SellableStockRow.
        var query = db.Database.SqlQuery<SellableStockRow>($"""
            SELECT organization_id, warehouse_id, product_id, brand, name, category, quality, ean, mpn, model_name, on_hand, reserved, available,
                   serialized, pos_price_minor
            FROM pos.sellable_stock
            """).Where(s => s.OrganizationId == OrganizationId && s.WarehouseId == warehouseId);
        return includeOutOfStock ? query : query.Where(s => s.Available > 0);
    }

    /// <summary>Products of the register's warehouse: in stock (available &gt; 0) unless <paramref name="includeOutOfStock"/>.</summary>
    public async Task<Result<PagedResult<PosProductDto>>> ListAsync(Guid registerId, string? search, string? category, bool includeOutOfStock,
        int? page, int? pageSize, CancellationToken ct)
    {
        var warehouseId = await WarehouseOfAsync(registerId, ct);
        if (warehouseId is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        return await ListInWarehouseAsync(warehouseId.Value, search, category, includeOutOfStock, page, pageSize, ct);
    }

    /// <summary>Products of one of the organization's warehouses (quotations and invoices outside the till).</summary>
    public async Task<Result<PagedResult<PosProductDto>>> ListInWarehouseAsync(Guid warehouse, string? search, string? category, bool includeOutOfStock,
        int? page, int? pageSize, CancellationToken ct)
    {
        Guid? warehouseId = warehouse;
        var query = Stock(warehouseId.Value, includeOutOfStock);
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!Enum.TryParse<ProductCategory>(category.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Error.Validation("pos.category", $"Unknown category '{category}'.");
            var name = parsed.ToString();
            query = query.Where(s => s.Category == name);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (search.Trim().Length > 100)
                return Error.Validation("pos.search", "The search is limited to 100 characters.");
            var pattern = Text.LikePattern(search);
            query = query.Where(s => EF.Functions.ILike(s.Brand, pattern) || EF.Functions.ILike(s.Name, pattern)
                                     || (s.Ean != null && EF.Functions.ILike(s.Ean, pattern)) || (s.Mpn != null && EF.Functions.ILike(s.Mpn, pattern))
                                     || (s.ModelName != null && EF.Functions.ILike(s.ModelName, pattern)));
        }

        var (p, size) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(s => s.Brand).ThenBy(s => s.Name).ThenBy(s => s.ProductId).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var productIds = rows.Select(r => r.ProductId).ToList();
        var resolved = await prices.ResolveAsync(warehouseId.Value, productIds, ct);
        // Thumbnail of the primary image via the Catalog contract (one query for the page).
        var images = await catalog.GetImagesAsync(productIds, ct);
        var items = rows.Select(r =>
        {
            var (price, pos, _) = resolved[r.ProductId];
            return new PosProductDto(r.ProductId, r.Brand, r.Name, r.Category, r.Quality, r.Ean, r.Mpn, PrimaryThumbnail(images, r.ProductId), r.Serialized,
                r.OnHand, r.Reserved,
                r.Available, price.GrossMinor, price.Source.ToString(), pos);
        }).ToList();
        return new PagedResult<PosProductDto>(items, p, size, total);
    }

    /// <summary>Categories with in-stock products in the register's warehouse (for the till's category tabs).</summary>
    public async Task<Result<IReadOnlyList<PosCategoryDto>>> CategoriesAsync(Guid registerId, CancellationToken ct)
    {
        var warehouseId = await WarehouseOfAsync(registerId, ct);
        if (warehouseId is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var counts = await Stock(warehouseId.Value, includeOutOfStock: false).GroupBy(s => s.Category)
            .Select(g => new PosCategoryDto(g.Key, g.Count())).ToListAsync(ct);
        return counts.OrderBy(c => c.Category).ToList();
    }

    public async Task<Result<PosPriceDto>> GetPriceAsync(Guid productId, Guid? registerId, CancellationToken ct)
    {
        if (!(await catalog.GetAsync([productId], ct)).ContainsKey(productId))
            return Error.NotFound("pos.product_not_found", "Product not found.");
        var warehouseId = Guid.Empty;
        if (registerId is { } id)
        {
            if (await WarehouseOfAsync(id, ct) is not { } w)
                return Error.NotFound("pos.register_not_found", "Register not found.");
            warehouseId = w;
        }
        return await ToDtoAsync(productId, warehouseId, ct);
    }

    /// <summary>Sets (or with null clears) the organization's own POS price of a product. Changes are audited.</summary>
    public async Task<Result<PosPriceDto>> SetPriceAsync(Guid productId, SetPosPriceRequest request, CancellationToken ct)
    {
        if (!(await catalog.GetAsync([productId], ct)).ContainsKey(productId))
            return Error.NotFound("pos.product_not_found", "Product not found.");
        var existing = await db.Set<PosPrice>().FirstOrDefaultAsync(p => p.OrganizationId == OrganizationId && p.ProductId == productId, ct);
        if (existing is null)
        {
            var created = PosPrice.Create(OrganizationId, productId, request.PriceMinor, actor.UserId, clock.UtcNow);
            if (!created.IsSuccess)
                return created.Error!;
            db.Add(created.Value);
        }
        else
        {
            var changed = existing.Change(request.PriceMinor, actor.UserId, clock.UtcNow);
            if (!changed.IsSuccess)
                return changed.Error!;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("concurrency", "The price was changed concurrently. Reload and try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The price was changed concurrently. Reload and try again.");
        }
        return await ToDtoAsync(productId, Guid.Empty, ct);
    }

    private async Task<PosPriceDto> ToDtoAsync(Guid productId, Guid warehouseId, CancellationToken ct)
    {
        var (price, pos, net) = (await prices.ResolveAsync(warehouseId, [productId], ct))[productId];
        var updatedAt = await db.Set<PosPrice>().AsNoTracking().Where(p => p.OrganizationId == OrganizationId && p.ProductId == productId)
            .Select(p => (DateTimeOffset?)p.UpdatedAt).FirstOrDefaultAsync(ct);
        return new PosPriceDto(productId, pos, net, price.GrossMinor, price.Source.ToString(), updatedAt);
    }

    /// <summary>The primary image's thumbnail URL (relative to the API base), or null when the product has no image.</summary>
    internal static string? PrimaryThumbnail(IReadOnlyDictionary<Guid, IReadOnlyList<ProductImageInfo>> images, Guid productId) =>
        images.GetValueOrDefault(productId) is { Count: > 0 } list ? (list.FirstOrDefault(i => i.IsPrimary) ?? list[0]).ThumbnailUrl : null;

    private async Task<Guid?> WarehouseOfAsync(Guid registerId, CancellationToken ct) =>
        await db.Set<Register>().AsNoTracking().Where(r => r.Id == registerId && r.OrganizationId == OrganizationId)
            .Select(r => (Guid?)r.WarehouseId).FirstOrDefaultAsync(ct);
}
