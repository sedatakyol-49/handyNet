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
}
