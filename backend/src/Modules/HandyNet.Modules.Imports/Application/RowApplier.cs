using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Imports.Application;

/// <summary>
/// The apply path shared by the import commit and the manual Lager entry, so both identify and create products and
/// stage offers in exactly the same way (conservative <see cref="ProductMatcher"/>: identifiers that disagree are a
/// conflict, never a merge).
/// </summary>
internal static class RowApplier
{
    public static Result<(Guid ProductId, bool Created)> ResolveProduct(IProductCatalog catalog, ProductMatcher matcher, ProductKey key,
        NewProduct product, Guid organizationId)
    {
        var match = matcher.Match(key);
        switch (match.Kind)
        {
            case MatchKind.Existing:
                return (match.ProductId!.Value, false);
            case MatchKind.New:
                var staged = catalog.StageNewProduct(product, organizationId);
                if (!staged.IsSuccess)
                    return staged.Error!;
                matcher.Add(staged.Value);
                return (staged.Value.Id, true);
            default:
                return Error.Conflict("product.identifier_conflict", match.Reason!);
        }
    }

    public static Task<Result<Guid>> StageOfferAsync(IOfferSync offers, string sku, Guid productId, Guid warehouseId,
        Marketplace.Domain.OfferTerms terms, bool publish, CancellationToken ct) =>
        offers.StageUpsertAsync(new OfferUpsert(sku, productId, warehouseId, terms, publish), ct);
}
