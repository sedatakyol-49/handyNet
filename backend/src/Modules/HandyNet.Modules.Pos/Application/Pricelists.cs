using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Create/update body of a price list. Version: required on update (optimistic concurrency). Dates are Europe/Berlin days, inclusive.</summary>
public sealed record PricelistRequest(string Name, int DiscountBasisPoints = 0, bool IsActive = true, DateOnly? ValidFrom = null, DateOnly? ValidTo = null,
    uint? Version = null);

/// <summary>CurrentlyValid: active and inside its validity today — only then it changes a customer's prices.</summary>
public sealed record PricelistDto(
    Guid Id,
    string Name,
    int DiscountBasisPoints,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool CurrentlyValid,
    int ItemCount,
    int CustomerCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed record PricelistItemRequest(long PriceMinor);

/// <summary>BasePriceMinor: the regular till price of the product (own POS price, else own offer; null = none), for comparison.</summary>
public sealed record PricelistItemDto(Guid ProductId, string Brand, string Name, string? Ean, long PriceMinor, long? BasePriceMinor, DateTimeOffset UpdatedAt);

internal sealed class PricelistValidator : AbstractValidator<PricelistRequest>
{
    public PricelistValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Pricelist.MaxNameLength);
        RuleFor(x => x.DiscountBasisPoints).InclusiveBetween(0, Pricelist.MaxDiscountBasisPoints);
    }
}

internal sealed class PricelistItemValidator : AbstractValidator<PricelistItemRequest>
{
    public PricelistItemValidator() => RuleFor(x => x.PriceMinor).InclusiveBetween(1, PosPricing.MaxPriceMinor);
}

/// <summary>Price lists and their item prices (ADR-0020): read with <c>pos.sell</c>, written with <c>pos.manage</c>.</summary>
internal sealed class PricelistService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IProductCatalog catalog, PriceResolver prices,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    private static readonly Error NotFound = Error.NotFound("pos.pricelist_not_found", "Price list not found.");

    public async Task<IReadOnlyList<PricelistDto>> ListAsync(CancellationToken ct)
    {
        var lists = await db.Set<Pricelist>().AsNoTracking().Where(p => p.OrganizationId == OrganizationId).OrderBy(p => p.Name).ToListAsync(ct);
        var items = await db.Set<PricelistItem>().Where(i => i.OrganizationId == OrganizationId).GroupBy(i => i.PricelistId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var customers = await db.Set<Customer>().Where(c => c.OrganizationId == OrganizationId && c.PricelistId != null).GroupBy(c => c.PricelistId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var today = BerlinTime.Today(clock.UtcNow);
        return lists.Select(p => ToDto(p, items.GetValueOrDefault(p.Id), customers.GetValueOrDefault(p.Id), today)).ToList();
    }

    public async Task<Result<PricelistDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<Pricelist>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct) is { } pricelist
            ? await ToDtoAsync(pricelist, ct)
            : NotFound;

    public async Task<Result<PricelistDto>> CreateAsync(PricelistRequest request, CancellationToken ct)
    {
        var pricelist = Pricelist.Create(OrganizationId, request.Name, request.DiscountBasisPoints, request.IsActive, request.ValidFrom, request.ValidTo, clock.UtcNow);
        if (!pricelist.IsSuccess)
            return pricelist.Error!;
        db.Add(pricelist.Value);
        var saved = await SaveAsync(ct);
        return saved.IsSuccess ? await ToDtoAsync(pricelist.Value, ct) : saved.Error!;
    }

    public async Task<Result<PricelistDto>> UpdateAsync(Guid id, PricelistRequest request, CancellationToken ct)
    {
        if (request.Version is not { } expected)
            return Error.Validation("pos.pricelist_version", "The version of the price list is required.");
        var pricelist = await db.Set<Pricelist>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (pricelist is null)
            return NotFound;
        db.Entry(pricelist).Property(p => p.Version).OriginalValue = expected;
        var updated = pricelist.Update(request.Name, request.DiscountBasisPoints, request.IsActive, request.ValidFrom, request.ValidTo, clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        var saved = await SaveAsync(ct);
        return saved.IsSuccess ? await ToDtoAsync(pricelist, ct) : saved.Error!;
    }

    /// <summary>Item prices with product data, by brand and name; search over brand, name and EAN.</summary>
    public async Task<Result<PagedResult<PricelistItemDto>>> ItemsAsync(Guid id, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await db.Set<Pricelist>().AnyAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct))
            return NotFound;
        if (search?.Trim().Length > 100)
            return Error.Validation("pos.search", "The search is limited to 100 characters.");
        var (p, size) = Paging.Normalize(page, pageSize);
        // A price list holds at most MaxItems prices; product texts come from the Catalog contract, so search and order run here.
        var items = await db.Set<PricelistItem>().AsNoTracking().Where(i => i.PricelistId == id && i.OrganizationId == OrganizationId).ToListAsync(ct);
        var products = await catalog.GetAsync(items.Select(i => i.ProductId).ToList(), ct);
        var term = search?.Trim();
        var rows = items.Where(i => products.ContainsKey(i.ProductId)).Select(i => (Item: i, Product: products[i.ProductId]))
            .Where(r => string.IsNullOrEmpty(term) || r.Product.Brand.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.Product.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (r.Product.Ean?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(r => r.Product.Brand, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Product.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item.ProductId)
            .ToList();
        var pageRows = rows.Skip((p - 1) * size).Take(size).ToList();
        var basePrices = await prices.ResolveAsync(Guid.Empty, pageRows.Select(r => r.Item.ProductId).ToList(), ct);
        return new PagedResult<PricelistItemDto>(
            pageRows.Select(r => ToDto(r.Item, r.Product, basePrices[r.Item.ProductId].Price.GrossMinor)).ToList(), p, size, rows.Count);
    }

    /// <summary>Sets (creates or changes) the price of a product on the price list.</summary>
    public async Task<Result<PricelistItemDto>> SetItemAsync(Guid id, Guid productId, PricelistItemRequest request, CancellationToken ct)
    {
        var pricelist = await db.Set<Pricelist>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (pricelist is null)
            return NotFound;
        if (!(await catalog.GetAsync([productId], ct)).TryGetValue(productId, out var product))
            return Error.NotFound("pos.product_not_found", "Product not found.");
        var item = await db.Set<PricelistItem>().FirstOrDefaultAsync(i => i.PricelistId == id && i.ProductId == productId, ct);
        if (item is null)
        {
            if (await db.Set<PricelistItem>().CountAsync(i => i.PricelistId == id, ct) >= Pricelist.MaxItems)
                return Error.Conflict("pos.pricelist_item_limit", $"A price list can hold at most {Pricelist.MaxItems} prices.");
            var created = PricelistItem.Create(pricelist, productId, request.PriceMinor, actor.UserId, clock.UtcNow);
            if (!created.IsSuccess)
                return created.Error!;
            item = created.Value;
            db.Add(item);
        }
        else if (item.Change(request.PriceMinor, actor.UserId, clock.UtcNow) is { IsSuccess: false } invalid)
        {
            return invalid.Error!;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("concurrency", "The price was changed concurrently. Reload and try again.");
        }
        var basePrice = (await prices.ResolveAsync(Guid.Empty, [productId], ct))[productId].Price.GrossMinor;
        return ToDto(item, product, basePrice);
    }

    public async Task<Result> DeleteItemAsync(Guid id, Guid productId, CancellationToken ct)
    {
        if (!await db.Set<Pricelist>().AnyAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct))
            return NotFound;
        var item = await db.Set<PricelistItem>().FirstOrDefaultAsync(i => i.PricelistId == id && i.ProductId == productId && i.OrganizationId == OrganizationId, ct);
        if (item is null)
            return Error.NotFound("pos.pricelist_item_not_found", "This price list has no price for the product.");
        db.Remove(item);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("pos.pricelist_name_taken", "A price list with this name already exists.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The price list was changed concurrently. Reload and try again.");
        }
    }

    private async Task<PricelistDto> ToDtoAsync(Pricelist p, CancellationToken ct) =>
        ToDto(p, await db.Set<PricelistItem>().CountAsync(i => i.PricelistId == p.Id, ct),
            await db.Set<Customer>().CountAsync(c => c.OrganizationId == OrganizationId && c.PricelistId == p.Id, ct), BerlinTime.Today(clock.UtcNow));

    private static PricelistDto ToDto(Pricelist p, int items, int customers, DateOnly today) =>
        new(p.Id, p.Name, p.DiscountBasisPoints, p.IsActive, p.ValidFrom, p.ValidTo, p.AppliesOn(today), items, customers, p.CreatedAt, p.UpdatedAt, p.Version);

    private static PricelistItemDto ToDto(PricelistItem i, ProductSummary product, long? basePriceMinor) =>
        new(i.ProductId, product.Brand, product.Name, product.Ean, i.PriceMinor, basePriceMinor, i.UpdatedAt);
}

internal static class PricelistEndpoints
{
    public static void Map(RouteGroupBuilder sell, RouteGroupBuilder manage)
    {
        sell.MapGet("/pricelists", async (PricelistService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        sell.MapGet("/pricelists/{id:guid}", async (Guid id, PricelistService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapPost("/pricelists", async (PricelistRequest request, IValidator<PricelistRequest> validator, PricelistService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.CreateAsync(request, ct)).ToHttp(p => Results.Created($"/api/pos/pricelists/{p.Id}", p)));

        manage.MapPut("/pricelists/{id:guid}", async (Guid id, PricelistRequest request, IValidator<PricelistRequest> validator, PricelistService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok));

        sell.MapGet("/pricelists/{id:guid}/items", async (Guid id, string? search, int? page, int? pageSize, PricelistService service, CancellationToken ct) =>
            (await service.ItemsAsync(id, search, page, pageSize, ct)).ToHttp(Results.Ok));

        manage.MapPut("/pricelists/{id:guid}/items/{productId:guid}", async (Guid id, Guid productId, PricelistItemRequest request,
                IValidator<PricelistItemRequest> validator, PricelistService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SetItemAsync(id, productId, request, ct)).ToHttp(Results.Ok));

        manage.MapDelete("/pricelists/{id:guid}/items/{productId:guid}", async (Guid id, Guid productId, PricelistService service, CancellationToken ct) =>
            (await service.DeleteItemAsync(id, productId, ct)).ToHttp(Results.NoContent));
    }
}
