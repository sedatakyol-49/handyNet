using HandyNet.Modules.Catalog.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Catalog.Contracts;

public sealed record ProductSummary(
    Guid Id,
    string Brand,
    string Name,
    string Category,
    string Quality,
    string? Mpn,
    string? Ean,
    string? ModelName,
    IReadOnlyList<string> CompatibleModels);

/// <summary>
/// A product image as clients see it. URLs are relative to the API base and serve immutable content (cache forever);
/// they are public on purpose so plain &lt;img&gt; tags work (ADR-0011).
/// </summary>
public sealed record ProductImageInfo(
    Guid Id,
    int Position,
    bool IsPrimary,
    int Width,
    int Height,
    int ThumbnailWidth,
    int ThumbnailHeight,
    string ThumbnailUrl,
    string FullUrl);

public static class ProductImageUrls
{
    public static string Thumbnail(Guid imageId) => $"/api/catalog/images/{imageId}/thumb";
    public static string Full(Guid imageId) => $"/api/catalog/images/{imageId}/full";
}

/// <summary>How a lookup found the product: Ean (exact GTIN), Mpn (exact part number key) or Text (name/brand/model).</summary>
public sealed record ProductLookupMatch(ProductSummary Product, string MatchedBy, bool CreatedByCurrentOrganization);

/// <summary>Public API of the Catalog module.</summary>
public interface IProductCatalog
{
    /// <summary>Loads every product that could match one of the keys (by EAN or brand+MPN+quality).</summary>
    Task<ProductMatcher> CreateMatcherAsync(IReadOnlyCollection<ProductKey> keys, CancellationToken ct);

    /// <summary>Adds a product to the shared unit of work; the caller commits.</summary>
    Result<ProductRef> StageNewProduct(NewProduct product, Guid? createdByOrganizationId);

    /// <summary>
    /// Finds or stages the catalog product for a whole device (e.g. a phone bought back from a consumer). Used devices
    /// rarely come with an EAN, so the identity is brand + model designation (+ storage) as manufacturer part number.
    /// </summary>
    Task<Result<ProductRef>> FindOrStageDeviceAsync(string brand, string model, string? storage, Guid? createdByOrganizationId, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, ProductSummary>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    /// <summary>Finds a product by a scanned EAN/GTIN (check digit validated); null if invalid or unknown.</summary>
    Task<ProductSummary?> FindByEanAsync(string code, CancellationToken ct);

    /// <summary>
    /// Step 1 of manual entry: exact EAN/GTIN match first, then exact part-number matches (any brand/quality), then
    /// text matches on brand/name/model. At most <paramref name="limit"/> results, no duplicates.
    /// </summary>
    Task<IReadOnlyList<ProductLookupMatch>> LookupAsync(string query, int limit, CancellationToken ct);

    /// <summary>Active images per product, primary first. Products without images are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ProductImageInfo>>> GetImagesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}
