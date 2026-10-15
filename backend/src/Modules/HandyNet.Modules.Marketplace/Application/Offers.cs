using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Marketplace.Application;

public sealed record OfferInput(
    Guid ProductId,
    Guid? WarehouseId,
    string SupplierSku,
    decimal Price,
    int MinOrderQuantity,
    int DeliveryDays,
    OfferCondition Condition,
    bool Publish);

public sealed record UpdateOfferRequest(decimal Price, int MinOrderQuantity, int DeliveryDays, OfferCondition Condition, OfferStatus? Status);

public sealed record OwnOfferDto(
    Guid Id,
    Guid ProductId,
    string Brand,
    string ProductName,
    string? Ean,
    string? Mpn,
    string Quality,
    Guid WarehouseId,
    string SupplierSku,
    long PriceMinor,
    string Currency,
    int MinOrderQuantity,
    int DeliveryDays,
    string Condition,
    string Status,
    int OnHand,
    int Reserved,
    DateTimeOffset UpdatedAt);

internal sealed class OfferInputValidator : AbstractValidator<OfferInput>
{
    public OfferInputValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.SupplierSku).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Price).GreaterThan(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.MinOrderQuantity).GreaterThanOrEqualTo(1);
        RuleFor(x => x.DeliveryDays).InclusiveBetween(0, Offer.MaxDeliveryDays);
        RuleFor(x => x.Condition).IsInEnum();
    }
}

internal sealed class UpdateOfferValidator : AbstractValidator<UpdateOfferRequest>
{
    public UpdateOfferValidator()
    {
        RuleFor(x => x.Price).GreaterThan(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.MinOrderQuantity).GreaterThanOrEqualTo(1);
        RuleFor(x => x.DeliveryDays).InclusiveBetween(0, Offer.MaxDeliveryDays);
        RuleFor(x => x.Condition).IsInEnum();
        RuleFor(x => x.Status).IsInEnum();
    }
}

internal sealed class OfferService(
    HandyNetDbContext db,
    ITenantContext tenant,
    IProductCatalog catalog,
    IInventory inventory,
    IClock clock) : IOfferSync, IOfferReservations
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    // ---- own offers ---------------------------------------------------------------------------

    public async Task<PagedResult<OwnOfferDto>> ListOwnAsync(string? search, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Offer>().AsNoTracking().Where(o => o.OrganizationId == OrganizationId);
        if (Enum.TryParse<OfferStatus>(status, true, out var parsed))
            query = query.Where(o => o.Status == parsed);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var key = Text.NormalizeKey(search);
            query = query.Where(o => o.SkuKey.Contains(key));
        }

        var total = await query.CountAsync(ct);
        var offers = await query.OrderByDescending(o => o.UpdatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<OwnOfferDto>(await ToDtosAsync(offers, ct), p, size, total);
    }

    public async Task<Result<OwnOfferDto>> CreateAsync(OfferInput input, CancellationToken ct)
    {
        if (!Money.TryFromMajor(input.Price, Money.Eur, out var price))
            return Error.Validation("offer.price", "Price must have at most two decimal places.");
        if ((await catalog.GetAsync([input.ProductId], ct)).Count == 0)
            return Error.NotFound("product.not_found", "Product not found.");

        var warehouseId = input.WarehouseId ?? await inventory.GetOrStageDefaultWarehouseAsync(ct);
        if (input.WarehouseId is not null && !await inventory.IsOwnWarehouseAsync(warehouseId, ct))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");

        var created = Offer.Create(OrganizationId, input.ProductId, warehouseId, input.SupplierSku,
            new OfferTerms(price, input.MinOrderQuantity, input.DeliveryDays, input.Condition), input.Publish, clock.UtcNow);
        if (!created.IsSuccess)
            return created.Error!;

        db.Add(created.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("offer.sku_taken", "You already have an offer with this SKU.");
        }

        return (await ToDtosAsync([created.Value], ct))[0];
    }

    public async Task<Result<OwnOfferDto>> UpdateAsync(Guid id, UpdateOfferRequest request, CancellationToken ct)
    {
        if (!Money.TryFromMajor(request.Price, Money.Eur, out var price))
            return Error.Validation("offer.price", "Price must have at most two decimal places.");

        var offer = await db.Set<Offer>().FirstOrDefaultAsync(o => o.Id == id && o.OrganizationId == OrganizationId, ct);
        if (offer is null)
            return Error.NotFound("offer.not_found", "Offer not found.");

        var updated = offer.UpdateTerms(new OfferTerms(price, request.MinOrderQuantity, request.DeliveryDays, request.Condition), clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        if (request.Status is { } status && status != offer.Status)
        {
            var changed = offer.ChangeStatus(status, clock.UtcNow);
            if (!changed.IsSuccess)
                return changed.Error!;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The offer was changed concurrently. Reload and try again.");
        }

        return (await ToDtosAsync([offer], ct))[0];
    }

    private async Task<List<OwnOfferDto>> ToDtosAsync(IReadOnlyList<Offer> offers, CancellationToken ct)
    {
        var products = await catalog.GetAsync(offers.Select(o => o.ProductId).Distinct().ToList(), ct);
        var stock = new Dictionary<(Guid, Guid), StockSnapshot>();
        foreach (var warehouse in offers.GroupBy(o => o.WarehouseId))
        {
            var levels = await inventory.GetLevelsAsync(warehouse.Key, warehouse.Select(o => o.ProductId).Distinct().ToList(), ct);
            foreach (var level in levels.Values)
                stock[(level.WarehouseId, level.ProductId)] = level;
        }

        return offers.Select(o =>
        {
            var product = products[o.ProductId];
            stock.TryGetValue((o.WarehouseId, o.ProductId), out var level);
            return new OwnOfferDto(o.Id, o.ProductId, product.Brand, product.Name, product.Ean, product.Mpn, product.Quality,
                o.WarehouseId, o.SupplierSku, o.PriceMinor, o.Currency, o.MinOrderQuantity, o.DeliveryDays,
                o.Condition.ToString(), o.Status.ToString(), level?.OnHand ?? 0, level?.Reserved ?? 0, o.UpdatedAt);
        }).ToList();
    }

    // ---- IOfferSync (imports) ---------------------------------------------------------------

    public async Task<IReadOnlyDictionary<string, ExistingOffer>> GetBySkuKeysAsync(IReadOnlyCollection<string> skuKeys, CancellationToken ct) =>
        await db.Set<Offer>().AsNoTracking()
            .Where(o => o.OrganizationId == OrganizationId && skuKeys.Contains(o.SkuKey))
            .Select(o => new ExistingOffer(o.Id, o.SkuKey, o.ProductId, o.PriceMinor, o.MinOrderQuantity, o.DeliveryDays, o.Condition, o.Status))
            .ToDictionaryAsync(o => o.SkuKey, ct);

    public async Task<Result<Guid>> StageUpsertAsync(OfferUpsert upsert, CancellationToken ct)
    {
        var skuKey = Text.NormalizeKey(upsert.SupplierSku);
        var offer = await db.Set<Offer>().FirstOrDefaultAsync(o => o.OrganizationId == OrganizationId && o.SkuKey == skuKey, ct);

        if (offer is null)
        {
            var created = Offer.Create(OrganizationId, upsert.ProductId, upsert.WarehouseId, upsert.SupplierSku,
                upsert.Terms, upsert.Publish, clock.UtcNow);
            if (!created.IsSuccess)
                return created.Error!;
            db.Add(created.Value);
            return created.Value.Id;
        }

        if (offer.ProductId != upsert.ProductId)
            return Error.Conflict("offer.product_changed",
                $"SKU {upsert.SupplierSku} is already linked to a different product. Archive the old offer first.");

        var updated = offer.UpdateTerms(upsert.Terms, clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        if (upsert.Publish && offer.Status is OfferStatus.Draft)
            offer.ChangeStatus(OfferStatus.Published, clock.UtcNow);
        return offer.Id;
    }

    // ---- IOfferReservations (ordering) & browsing --------------------------------------------

    public async Task<IReadOnlyDictionary<Guid, ListingDto>> GetListingsAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct) =>
        (await db.Set<OfferListing>().AsNoTracking().Where(l => offerIds.Contains(l.OfferId)).ToListAsync(ct))
        .ToDictionary(l => l.OfferId, ToDto);

    public async Task<Guid?> ReserveAsync(Guid offerId, int quantity, string reference, CancellationToken ct) =>
        await db.Database
            .SqlQuery<Guid?>($"SELECT marketplace.reserve_offer_stock({offerId}, {quantity}, {reference}) AS \"Value\"")
            .SingleAsync(ct);

    public async Task<PagedResult<ListingDto>> BrowseAsync(BrowseQuery q, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = db.Set<OfferListing>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = Text.LikePattern(q.Search);
            var key = Text.NormalizeKey(q.Search);
            query = query.Where(l => EF.Functions.ILike(l.ProductName, pattern) || EF.Functions.ILike(l.Brand, pattern)
                                     || l.Ean == key || (l.Mpn != null && EF.Functions.ILike(l.Mpn, pattern))
                                     || EF.Functions.ILike(l.SupplierSku, pattern));
        }
        if (!string.IsNullOrWhiteSpace(q.Category))
            query = query.Where(l => l.Category == q.Category);
        if (!string.IsNullOrWhiteSpace(q.Brand))
            query = query.Where(l => l.Brand == q.Brand);
        if (q.ProductId is { } productId)
            query = query.Where(l => l.ProductId == productId);
        if (q.InStockOnly == true)
            query = query.Where(l => l.Available > 0);

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

    private static ListingDto ToDto(OfferListing l) => new(
        l.OfferId, l.SellerOrganizationId, l.SellerName, l.SellerCity, l.ProductId, l.Brand, l.ProductName, l.Category,
        l.Quality, l.Ean, l.Mpn, l.SupplierSku, l.PriceMinor, l.Currency, l.MinOrderQuantity, l.DeliveryDays, l.Condition,
        l.Available, l.UpdatedAt);
}

public sealed record BrowseQuery(
    string? Search,
    string? Category,
    string? Brand,
    Guid? ProductId,
    bool? InStockOnly,
    string? Sort,
    int? Page,
    int? PageSize);
