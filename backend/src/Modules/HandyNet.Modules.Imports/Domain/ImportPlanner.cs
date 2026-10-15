using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;

namespace HandyNet.Modules.Imports.Domain;

public sealed record PlannedRow(
    ParsedRow Parsed,
    RowStatus Status,
    IReadOnlyList<string> Messages,
    ProductAction ProductAction,
    Guid? ProductId,
    OfferAction OfferAction,
    Guid? OfferId,
    long? OldPriceMinor,
    int? OldQuantity);

/// <summary>
/// Decides, without side effects, what an import would do with every row. The same planner runs for
/// the preview (dry run) and again inside the commit transaction against fresh data, so the commit
/// never acts on a stale preview.
/// </summary>
public static class ImportPlanner
{
    /// <summary>Price changes above this share are flagged for the reviewer.</summary>
    public const decimal LargePriceChange = 0.30m;

    public static IReadOnlyList<PlannedRow> Plan(
        IReadOnlyList<ParsedRow> rows,
        ProductMatcher matcher,
        IReadOnlyDictionary<string, ExistingOffer> existingOffers,
        IReadOnlyDictionary<Guid, StockSnapshot> stock,
        bool publishOffers)
    {
        var result = new List<PlannedRow>(rows.Count);
        var firstRowBySku = new Dictionary<string, int>();
        var pendingNewProducts = new Dictionary<Guid, int>(); // placeholder id → row that creates it

        foreach (var row in rows)
        {
            if (!row.IsValid)
            {
                result.Add(Rejected(row, RowStatus.Error, row.Errors));
                continue;
            }

            if (!firstRowBySku.TryAdd(row.SkuKey, row.RowNumber))
            {
                result.Add(Rejected(row, RowStatus.Error, [$"Duplicate SKU in file (first seen in row {firstRowBySku[row.SkuKey]})."]));
                continue;
            }

            var messages = new List<string>();
            var match = matcher.Match(row.ProductKey);
            if (match.Kind == MatchKind.Conflict)
            {
                result.Add(Rejected(row, RowStatus.Conflict, [match.Reason!]));
                continue;
            }

            ProductAction productAction;
            Guid? productId;
            if (match.Kind == MatchKind.New)
            {
                productAction = ProductAction.CreateNew;
                productId = null;
                var placeholder = Guid.NewGuid();
                pendingNewProducts[placeholder] = row.RowNumber;
                var key = row.ProductKey;
                matcher.Add(new ProductRef(placeholder, row.Brand!, row.Name!, key.BrandKey, key.MpnKey, key.Ean, key.Quality));
            }
            else if (pendingNewProducts.TryGetValue(match.ProductId!.Value, out var creatingRow))
            {
                productAction = ProductAction.CreateNew;
                productId = null;
                messages.Add($"Same new product as row {creatingRow}.");
            }
            else
            {
                productAction = ProductAction.MatchExisting;
                productId = match.ProductId;
            }

            existingOffers.TryGetValue(row.SkuKey, out var offer);
            if (offer is not null && offer.ProductId != productId)
            {
                result.Add(Rejected(row, RowStatus.Conflict,
                    [$"SKU {row.Sku} is already linked to a different product. Archive that offer or use a new SKU."]));
                continue;
            }

            OfferAction offerAction;
            if (offer is null)
                offerAction = OfferAction.Create;
            else
            {
                var priceChanged = offer.PriceMinor != row.Price!.Value.MinorUnits;
                var termsChanged = priceChanged
                                   || offer.DeliveryDays != row.DeliveryDays
                                   || offer.MinOrderQuantity != row.MinOrderQuantity
                                   || offer.Condition != row.Condition
                                   || (publishOffers && offer.Status == OfferStatus.Draft);
                offerAction = termsChanged ? OfferAction.Update : OfferAction.Unchanged;
                if (priceChanged)
                {
                    var change = Math.Abs(row.Price.Value.MinorUnits - offer.PriceMinor) / (decimal)offer.PriceMinor;
                    if (change > LargePriceChange)
                        messages.Add(FormattableString.Invariant($"Price changes by {change * 100:0}% (from {offer.PriceMinor / 100m:0.00} EUR)."));
                }
            }

            int? oldQuantity = null;
            if (row.Quantity is { } quantity && productId is { } existingProduct && stock.TryGetValue(existingProduct, out var level))
            {
                oldQuantity = level.OnHand;
                if (quantity < level.Reserved)
                {
                    result.Add(Rejected(row, RowStatus.Error,
                        [$"Quantity {quantity} is below the {level.Reserved} units reserved for open orders."]));
                    continue;
                }
            }

            result.Add(new PlannedRow(row, RowStatus.Valid, messages, productAction, productId, offerAction,
                offer?.Id, offer?.PriceMinor, oldQuantity));
        }

        return result;
    }

    public static ImportSummary Summarize(IReadOnlyList<PlannedRow> rows)
    {
        var valid = rows.Where(r => r.Status == RowStatus.Valid).ToList();
        return new ImportSummary(
            TotalRows: rows.Count,
            ValidRows: valid.Count,
            ErrorRows: rows.Count(r => r.Status == RowStatus.Error),
            ConflictRows: rows.Count(r => r.Status == RowStatus.Conflict),
            NewProducts: valid.Count(r => r.ProductAction == ProductAction.CreateNew && !r.Messages.Any(m => m.StartsWith("Same new product", StringComparison.Ordinal))),
            NewOffers: valid.Count(r => r.OfferAction == OfferAction.Create),
            UpdatedOffers: valid.Count(r => r.OfferAction == OfferAction.Update),
            UnchangedOffers: valid.Count(r => r.OfferAction == OfferAction.Unchanged),
            StockChanges: valid.Count(r => r.Parsed.Quantity is { } q && q != (r.OldQuantity ?? 0)));
    }

    private static PlannedRow Rejected(ParsedRow row, RowStatus status, IReadOnlyList<string> messages) =>
        new(row, status, messages, ProductAction.None, null, OfferAction.None, null, null, null);
}
