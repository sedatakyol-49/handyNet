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

    Task<IReadOnlyDictionary<Guid, ProductSummary>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}
