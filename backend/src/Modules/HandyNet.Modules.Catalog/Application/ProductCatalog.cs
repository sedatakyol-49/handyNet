using HandyNet.SharedKernel.Web;
using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Catalog.Application;

public sealed record CreateProductRequest(
    string Brand,
    string Name,
    string Category,
    string? Quality,
    string? Mpn,
    string? Ean,
    string? ModelName,
    string[]? CompatibleModels);

internal sealed class CreateProductValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Brand).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Category).Must(c => CategoryParser.TryParse(c, out _)).WithMessage("Unknown category.").WithErrorCode("validation.category");
        RuleFor(x => x.Quality).Must(q => QualityParser.TryParse(q, out _)).WithMessage("Unknown part quality.").WithErrorCode("validation.quality");
        RuleFor(x => x.Mpn).MaximumLength(100);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Ean) || !string.IsNullOrWhiteSpace(x.Mpn))
            .WithName("Identifier").WithMessage("Provide an EAN/GTIN or a manufacturer part number.").WithErrorCode("validation.identifier");
    }
}

/// <summary>
/// Full replacement of a product's data by the organization that created it. <paramref name="Version"/> is the
/// <c>version</c> from <c>GET /api/catalog/products/{id}</c> (optimistic concurrency: 409 <c>concurrency</c> when stale).
/// </summary>
public sealed record UpdateProductRequest(
    string Brand,
    string Name,
    string Category,
    string? Quality,
    string? Mpn,
    string? Ean,
    string? ModelName,
    string[]? CompatibleModels,
    uint? Version);

/// <summary>
/// A product with what the current organization may do with it: <c>CreatedByCurrentOrganization</c> (owner of the
/// canonical data and images) and <c>CanEdit</c> (owner and module ProductImport + permission imports.manage).
/// </summary>
public sealed record ProductDetailDto(
    Guid Id,
    string Brand,
    string Name,
    string Category,
    string Quality,
    string? Mpn,
    string? Ean,
    string? ModelName,
    IReadOnlyList<string> CompatibleModels,
    bool CreatedByCurrentOrganization,
    bool CanEdit,
    uint Version);

internal sealed class UpdateProductValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Brand).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Category).Must(c => CategoryParser.TryParse(c, out _)).WithMessage("Unknown category.").WithErrorCode("validation.category");
        RuleFor(x => x.Quality).Must(q => QualityParser.TryParse(q, out _)).WithMessage("Unknown part quality.").WithErrorCode("validation.quality");
        RuleFor(x => x.Mpn).MaximumLength(100);
        RuleFor(x => x.Version).NotNull();
        RuleFor(x => x.ModelName).MaximumLength(200);
        RuleFor(x => x.CompatibleModels).MaxCount(m => m!.Length, Product.MaxCompatibleModels, $"At most {Product.MaxCompatibleModels} compatible models.");
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Ean) || !string.IsNullOrWhiteSpace(x.Mpn))
            .WithName("Identifier").WithMessage("Provide an EAN/GTIN or a manufacturer part number.").WithErrorCode("validation.identifier");
    }
}

internal sealed class ProductCatalog(HandyNetDbContext db, IClock clock, ITenantContext tenant) : IProductCatalog
{
    public async Task<ProductMatcher> CreateMatcherAsync(IReadOnlyCollection<ProductKey> keys, CancellationToken ct)
    {
        var eans = keys.Where(k => k.Ean is not null).Select(k => k.Ean!).Distinct().ToList();
        var mpnKeys = keys.Where(k => k.MpnKey is not null).Select(k => k.MpnKey!).Distinct().ToList();

        var candidates = await db.Set<Product>().AsNoTracking()
            .Where(p => (p.Ean != null && eans.Contains(p.Ean)) || (p.MpnKey != null && mpnKeys.Contains(p.MpnKey)))
            .Select(p => new ProductRef(p.Id, p.Brand, p.Name, p.BrandKey, p.MpnKey, p.Ean, p.Quality))
            .ToListAsync(ct);

        // Products staged earlier in the same unit of work (e.g. earlier rows of the same import).
        candidates.AddRange(db.ChangeTracker.Entries<Product>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Select(p => new ProductRef(p.Id, p.Brand, p.Name, p.BrandKey, p.MpnKey, p.Ean, p.Quality)));

        return new ProductMatcher(candidates);
    }

    public async Task<Result<ProductRef>> FindOrStageDeviceAsync(string brand, string model, string? storage, Guid? createdByOrganizationId,
        CancellationToken ct)
    {
        var designation = string.IsNullOrWhiteSpace(storage) ? model.Trim() : $"{model.Trim()} {storage.Trim()}";
        var key = new ProductKey(Text.NormalizeKey(brand), Text.NormalizeKey(designation), null, PartQuality.NotApplicable);
        var match = (await CreateMatcherAsync([key], ct)).Match(key);
        return match.Kind switch
        {
            MatchKind.Existing => (await db.Set<Product>().AsNoTracking().Where(p => p.Id == match.ProductId)
                    .Select(p => new ProductRef(p.Id, p.Brand, p.Name, p.BrandKey, p.MpnKey, p.Ean, p.Quality)).FirstOrDefaultAsync(ct))
                ?? db.ChangeTracker.Entries<Product>().Select(e => e.Entity).Where(p => p.Id == match.ProductId)
                    .Select(p => new ProductRef(p.Id, p.Brand, p.Name, p.BrandKey, p.MpnKey, p.Ean, p.Quality)).First(),
            MatchKind.New => StageNewProduct(new NewProduct(brand, designation, ProductCategory.Smartphone,
                PartQuality.NotApplicable, designation, null, model.Trim()), createdByOrganizationId),
            _ => Error.Conflict("product.identifier_conflict", match.Reason!),
        };
    }

    public Result<ProductRef> StageNewProduct(NewProduct product, Guid? createdByOrganizationId)
    {
        var created = Product.Create(product, createdByOrganizationId, clock.UtcNow);
        if (!created.IsSuccess)
            return created.Error!;
        db.Add(created.Value);
        var p = created.Value;
        return new ProductRef(p.Id, p.Brand, p.Name, p.BrandKey, p.MpnKey, p.Ean, p.Quality);
    }

    public async Task<ProductSummary?> FindByEanAsync(string code, CancellationToken ct)
    {
        if (!Gtin.TryNormalize(code, out var ean))
            return null;
        var product = await db.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Ean == ean, ct);
        return product is null ? null : ToSummary(product);
    }

    public async Task<IReadOnlyList<ProductLookupMatch>> LookupAsync(string query, int limit, CancellationToken ct)
    {
        var trimmed = query.Trim();
        var results = new List<ProductLookupMatch>();
        if (trimmed.Length == 0 || limit <= 0)
            return results;

        void AddRange(IEnumerable<Product> products, string matchedBy)
        {
            foreach (var p in products)
                if (results.Count < limit && results.All(r => r.Product.Id != p.Id))
                    results.Add(new ProductLookupMatch(ToSummary(p), matchedBy, p.CreatedByOrganizationId == tenant.OrganizationId && tenant.OrganizationId is not null));
        }

        if (Gtin.TryNormalize(trimmed, out var ean))
            AddRange(await db.Set<Product>().AsNoTracking().Where(p => p.Ean == ean).ToListAsync(ct), "Ean");

        var key = Text.NormalizeKey(trimmed);
        if (key.Length > 0 && results.Count < limit)
            AddRange(await db.Set<Product>().AsNoTracking().Where(p => p.MpnKey == key)
                .OrderBy(p => p.Brand).ThenBy(p => p.Quality).Take(limit).ToListAsync(ct), "Mpn");

        if (trimmed.Length >= 2 && results.Count < limit)
        {
            var pattern = Text.LikePattern(trimmed);
            AddRange(await db.Set<Product>().AsNoTracking()
                .Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Brand, pattern)
                            || (p.ModelName != null && EF.Functions.ILike(p.ModelName, pattern)) || p.ModelKeys.Contains(key))
                .OrderBy(p => p.Brand).ThenBy(p => p.Name).Take(limit).ToListAsync(ct), "Text");
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ProductImageInfo>>> GetImagesAsync(IReadOnlyCollection<Guid> productIds,
        CancellationToken ct)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<ProductImageInfo>>();
        var images = await db.Set<ProductImage>().AsNoTracking()
            .Where(i => productIds.Contains(i.ProductId) && i.RemovedAt == null)
            .OrderBy(i => i.Position)
            .ToListAsync(ct);
        return images.GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductImageInfo>)g.Select(ProductImageService.ToInfo).ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, ProductSummary>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        (await db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(ct))
        .ToDictionary(p => p.Id, ToSummary);

    public async Task<PagedResult<ProductSummary>> SearchAsync(string? search, string? category, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Product>().AsNoTracking();
        if (CategoryParser.TryParse(category, out var parsedCategory))
            query = query.Where(x => x.Category == parsedCategory);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Text.LikePattern(search);
            var key = Text.NormalizeKey(search);
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) || EF.Functions.ILike(x.Brand, pattern)
                                     || x.Ean == key || x.MpnKey == key);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Brand).ThenBy(x => x.Name).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ProductSummary>(items.Select(ToSummary).ToList(), p, size, total);
    }

    public async Task<Result<ProductSummary>> CreateAsync(CreateProductRequest request, CancellationToken ct)
    {
        CategoryParser.TryParse(request.Category, out var category);
        QualityParser.TryParse(request.Quality, out var quality);
        var input = new NewProduct(request.Brand, request.Name, category, quality, request.Mpn, request.Ean,
            request.ModelName, request.CompatibleModels);

        var created = Product.Create(input, tenant.OrganizationId, clock.UtcNow);
        if (!created.IsSuccess)
            return created.Error!;

        var product = created.Value;
        var matcher = await CreateMatcherAsync([new ProductKey(product.BrandKey, product.MpnKey, product.Ean, product.Quality)], ct);
        var match = matcher.Match(new ProductKey(product.BrandKey, product.MpnKey, product.Ean, product.Quality));
        if (match.Kind == MatchKind.Existing)
            return Error.Conflict("product.exists", $"The product already exists ({match.ProductId}).");
        if (match.Kind == MatchKind.Conflict)
            return Error.Conflict("product.identifier_conflict", match.Reason!);

        db.Add(product);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("product.exists", "A product with this EAN or part number was created concurrently.");
        }

        return ToSummary(product);
    }

    public async Task<ProductDetailDto?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var product = await db.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return product is null ? null : ToDetail(product);
    }

    /// <summary>
    /// Changes the canonical data of a product the current organization created (ADR-0017). The new EAN and the new
    /// brand + part number + quality must not belong to another product (409 <c>product.ean_taken</c> /
    /// <c>product.identity_taken</c> with the other product's id). Audited through <see cref="IAuditable"/>.
    /// </summary>
    public async Task<Result<ProductDetailDto>> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken ct)
    {
        var product = await db.Set<Product>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
            return Error.NotFound("product.not_found", "Product not found.");
        if (!Product.CanEdit(product.CreatedByOrganizationId, tenant.OrganizationId))
            return Error.Forbidden("product.not_owner", "Only the organization that created the product can change its data.");
        if (product.Version != request.Version)
            return StaleVersion();

        CategoryParser.TryParse(request.Category, out var category);
        QualityParser.TryParse(request.Quality, out var quality);
        var (oldEan, oldBrandKey, oldMpnKey, oldQuality) = (product.Ean, product.BrandKey, product.MpnKey, product.Quality);
        var updated = product.Update(new NewProduct(request.Brand, request.Name, category, quality, request.Mpn, request.Ean,
            request.ModelName, request.CompatibleModels));
        if (!updated.IsSuccess)
            return updated.Error!;
        if (!updated.Value)
            return ToDetail(product);

        if (product.Ean is { } ean && ean != oldEan
            && await db.Set<Product>().AsNoTracking().Where(p => p.Id != id && p.Ean == ean).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct) is { } eanOwner)
            return EanTaken(ean, eanOwner);
        if (product.MpnKey is { } mpnKey && (mpnKey != oldMpnKey || product.BrandKey != oldBrandKey || product.Quality != oldQuality)
            && await db.Set<Product>().AsNoTracking()
                .Where(p => p.Id != id && p.BrandKey == product.BrandKey && p.MpnKey == mpnKey && p.Quality == product.Quality)
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct) is { } identityOwner)
            return IdentityTaken(identityOwner);

        // Optimistic concurrency: the row version (xmin) the client saw must still be current.
        db.Entry(product).Property(p => p.Version).OriginalValue = request.Version!.Value;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_products_identity"))
        {
            return IdentityTaken(null);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return EanTaken(product.Ean, null);
        }

        return ToDetail(product);

        static Error StaleVersion() => Error.Conflict("concurrency", "The product was changed in the meantime. Reload and try again.");
        static Error EanTaken(string? ean, Guid? other) => Error.Conflict("product.ean_taken", $"The EAN {ean} already belongs to another product.")
            .WithDetails(new Dictionary<string, object?> { ["productId"] = other });
        static Error IdentityTaken(Guid? other) => Error.Conflict("product.identity_taken",
                "Another product already has this brand, manufacturer part number and quality.")
            .WithDetails(new Dictionary<string, object?> { ["productId"] = other });
    }

    private ProductDetailDto ToDetail(Product p)
    {
        var own = Product.CanEdit(p.CreatedByOrganizationId, tenant.OrganizationId);
        var canEdit = own && tenant.HasModule(PlatformModule.ProductImport) && tenant.Permissions.Contains(Permissions.ImportsManage);
        return new ProductDetailDto(p.Id, p.Brand, p.Name, p.Category.ToString(), p.Quality.ToString(), p.Mpn, p.Ean, p.ModelName,
            p.CompatibleModels, own, canEdit, p.Version);
    }

    private static ProductSummary ToSummary(Product p) => new(
        p.Id, p.Brand, p.Name, p.Category.ToString(), p.Quality.ToString(), p.Mpn, p.Ean, p.ModelName, p.CompatibleModels);
}
