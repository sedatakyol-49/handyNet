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
        RuleFor(x => x.Category).Must(c => CategoryParser.TryParse(c, out _)).WithMessage("Unknown category.");
        RuleFor(x => x.Quality).Must(q => QualityParser.TryParse(q, out _)).WithMessage("Unknown part quality.");
        RuleFor(x => x.Mpn).MaximumLength(100);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Ean) || !string.IsNullOrWhiteSpace(x.Mpn))
            .WithName("Identifier").WithMessage("Provide an EAN/GTIN or a manufacturer part number.");
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

    public Result<ProductRef> StageNewProduct(NewProduct product, Guid? createdByOrganizationId)
    {
        var created = Product.Create(product, createdByOrganizationId, clock.UtcNow);
        if (!created.IsSuccess)
            return created.Error!;
        db.Add(created.Value);
        var p = created.Value;
        return new ProductRef(p.Id, p.Brand, p.Name, p.BrandKey, p.MpnKey, p.Ean, p.Quality);
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

    private static ProductSummary ToSummary(Product p) => new(
        p.Id, p.Brand, p.Name, p.Category.ToString(), p.Quality.ToString(), p.Mpn, p.Ean, p.ModelName, p.CompatibleModels);
}
