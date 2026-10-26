using HandyNet.SharedKernel.Web;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Imports.Domain;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Imei = HandyNet.Modules.Inventory.Domain.Imei;

namespace HandyNet.Modules.Imports.Application;

public sealed record ManualEntryProductInput(
    string Brand,
    string Name,
    string Category,
    string? Quality,
    string? Ean,
    string? Mpn,
    string? ModelName,
    string[]? CompatibleModels);

public sealed record ManualEntryOfferInput(
    string SupplierSku,
    decimal Price,
    int MinOrderQuantity = 1,
    int DeliveryDays = 2,
    OfferCondition Condition = OfferCondition.New,
    bool Publish = true);

/// <summary>
/// "Artikel anlegen": either <see cref="ProductId"/> or <see cref="NewProduct"/>; stock as <see cref="Quantity"/> (added
/// to on-hand as a goods receipt) or one IMEI per unit; optional net purchase price per unit (EK, EUR) and optional offer.
/// </summary>
public sealed record ManualEntryRequest(
    Guid? ProductId,
    ManualEntryProductInput? NewProduct,
    Guid? WarehouseId,
    int? Quantity,
    string[]? Imeis,
    decimal? PurchasePrice,
    ManualEntryOfferInput? Offer);

public sealed record EntryStockDto(Guid WarehouseId, int OnHand, int Reserved, int Available);

public sealed record EntryOfferDto(Guid Id, string Action, string SupplierSku, long PriceMinor, string Currency, int MinOrderQuantity,
    int DeliveryDays, string Condition, string Status);

public sealed record ManualEntryResultDto(
    Guid EntryId,
    string ProductAction,
    ProductSummary Product,
    int QuantityReceived,
    IReadOnlyList<Guid> UnitIds,
    long? UnitCostMinor,
    EntryStockDto Stock,
    EntryOfferDto? Offer,
    DateTimeOffset CreatedAt);

public sealed record ManualEntryResult(ManualEntryResultDto Entry, bool Replayed);

public sealed record LookupOwnOffer(Guid Id, string SupplierSku, long PriceMinor, int MinOrderQuantity, int DeliveryDays, string Condition, string Status);

public sealed record LookupMatchDto(
    ProductSummary Product,
    string MatchedBy,
    int ImageCount,
    string? PrimaryThumbnailUrl,
    bool CanManageImages,
    bool ImeiRequired,
    bool ImeiAllowed,
    int OwnOnHand,
    int OwnAvailable,
    int OwnSerializedUnits,
    IReadOnlyList<LookupOwnOffer> OwnOffers,
    string SuggestedSku);

public sealed record NewProductHint(string? Ean, string? Mpn);

public sealed record LookupResultDto(
    string Query,
    bool IsValidEan,
    string? NormalizedEan,
    bool LooksLikeImei,
    IReadOnlyList<LookupMatchDto> Matches,
    bool CanCreateProduct,
    bool CanCreateOffer,
    NewProductHint? NewProduct);

internal sealed class ManualEntryValidator : AbstractValidator<ManualEntryRequest>
{
    public ManualEntryValidator()
    {
        RuleFor(x => x).Must(x => (x.ProductId is null) != (x.NewProduct is null))
            .WithName("Product").WithMessage("Provide either productId or newProduct.").WithErrorCode("validation.productOrNew");
        When(x => x.NewProduct is not null, () =>
        {
            RuleFor(x => x.NewProduct!.Brand).NotEmpty().MaximumLength(100);
            RuleFor(x => x.NewProduct!.Name).NotEmpty().MaximumLength(300);
            RuleFor(x => x.NewProduct!.Category).Must(c => CategoryParser.TryParse(c, out _)).WithMessage("Unknown category.").WithErrorCode("validation.category");
            RuleFor(x => x.NewProduct!.Quality).Must(q => QualityParser.TryParse(q, out _)).WithMessage("Unknown part quality.").WithErrorCode("validation.quality");
            RuleFor(x => x.NewProduct!.Mpn).MaximumLength(100);
            RuleFor(x => x.NewProduct!.Ean).Must(e => string.IsNullOrWhiteSpace(e) || Gtin.TryNormalize(e, out _))
                .WithMessage("EAN/GTIN is invalid (length or check digit).").WithErrorCode("validation.gtin");
            RuleFor(x => x.NewProduct!).Must(p => !string.IsNullOrWhiteSpace(p.Ean) || !string.IsNullOrWhiteSpace(p.Mpn))
                .WithName("NewProduct.Identifier").WithMessage("Provide an EAN/GTIN or a manufacturer part number.").WithErrorCode("validation.identifier");
            RuleFor(x => x.NewProduct!.ModelName).MaximumLength(200);
            RuleFor(x => x.NewProduct!.CompatibleModels).MaxCount(m => m!.Length, Product.MaxCompatibleModels, $"At most {Product.MaxCompatibleModels} compatible models.");
            RuleForEach(x => x.NewProduct!.CompatibleModels).MaximumLength(200);
        });
        RuleFor(x => x.Quantity).InclusiveBetween(0, ManualEntryRules.MaxQuantity);
        RuleFor(x => x.Imeis).MaxCount(i => i!.Length, ManualEntryRules.MaxImeis, $"At most {ManualEntryRules.MaxImeis} IMEIs per entry.");
        RuleForEach(x => x.Imeis).Must(i => Imei.TryNormalize(i, out _)).WithMessage("IMEI '{PropertyValue}' must be 15 digits with a valid check digit.").WithErrorCode("validation.imei");
        RuleFor(x => x.Imeis).Must(i => i is null || i.Select(Normalize).Distinct().Count() == i.Length)
            .WithMessage("The same IMEI is listed twice.").WithErrorCode("validation.imeiDuplicate");
        RuleFor(x => x.PurchasePrice).GreaterThanOrEqualTo(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        When(x => x.Offer is not null, () =>
        {
            RuleFor(x => x.Offer!.SupplierSku).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Offer!.Price).GreaterThan(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
            RuleFor(x => x.Offer!.MinOrderQuantity).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Offer!.DeliveryDays).InclusiveBetween(0, Offer.MaxDeliveryDays);
            RuleFor(x => x.Offer!.Condition).IsInEnum();
        });
    }

    internal static string Normalize(string? imei) => Imei.TryNormalize(imei, out var normalized) ? normalized : imei ?? string.Empty;
}

/// <summary>
/// Manual single-item entry in Lager. Behaves like a one-row import: the same conservative matcher and product
/// creation (<see cref="RowApplier"/>), the same offer upsert by SKU, one transaction for product + stock (+ IMEI units)
/// + offer + the entry record, audited by the shared interceptors. Idempotent per organization and Idempotency-Key.
/// Differences to the file import, by design: stock is <em>received</em> (added to on-hand with a Receipt movement and
/// the EK), not synchronized to an absolute number; serialized devices are entered by IMEI.
/// </summary>
internal sealed class ManualEntryService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IProductCatalog catalog,
    IOfferSync offers,
    IInventory inventory,
    IClock clock,
    ILogger<ManualEntryService> logger)
{
    public const int MaxIdempotencyKeyLength = 100;
    public const int LookupLimit = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private Guid OrganizationId => tenant.RequiredOrganizationId;
    private bool CanCreateProducts => tenant.HasModule(PlatformModule.ProductImport) && tenant.Permissions.Contains(Permissions.ImportsManage);
    private bool CanCreateOffers => tenant.HasModule(PlatformModule.B2BSales) && tenant.Permissions.Contains(Permissions.OffersManage);

    public async Task<Result<ManualEntryResult>> CreateAsync(ManualEntryRequest request, string idempotencyKey, CancellationToken ct)
    {
        var imeis = (request.Imeis ?? []).Select(ManualEntryValidator.Normalize).ToList();
        var requestHash = HashRequest(request with { Imeis = imeis.ToArray() });
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } replay)
            return replay;

        if (request.NewProduct is not null && !CanCreateProducts)
            return Error.Forbidden("entry.product_creation_not_allowed",
                "Creating catalog products needs the ProductImport module and the imports.manage permission. Pick an existing product.");
        if (request.Offer is not null && !CanCreateOffers)
            return Error.Forbidden("entry.offers_not_allowed", "Creating offers needs the B2BSales module and the offers.manage permission.");

        long? unitCost = null;
        if (request.PurchasePrice is { } purchase)
        {
            if (!Money.TryFromMajor(purchase, Money.Eur, out var cost))
                return Error.Validation("entry.purchase_price", "The purchase price may have at most two decimal places.");
            unitCost = cost.MinorUnits;
        }
        OfferTerms? terms = null;
        if (request.Offer is { } offerInput)
        {
            if (!Money.TryFromMajor(offerInput.Price, Money.Eur, out var price))
                return Error.Validation("offer.price", "Price must have at most two decimal places.");
            terms = new OfferTerms(price, offerInput.MinOrderQuantity, offerInput.DeliveryDays, offerInput.Condition);
            if (terms.Validate() is { } invalidTerms)
                return invalidTerms;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        Guid warehouseId;
        if (request.WarehouseId is { } requestedWarehouse)
        {
            if (!await inventory.IsOwnWarehouseAsync(requestedWarehouse, ct))
                return Error.NotFound("warehouse.not_found", "Warehouse not found.");
            warehouseId = requestedWarehouse;
        }
        else
        {
            warehouseId = await inventory.GetOrStageDefaultWarehouseAsync(ct);
        }

        // ---- product: selected, matched or created (same path as the import commit) ----------------------------------
        Guid productId;
        EntryProductAction productAction;
        ProductCategory category;
        if (request.ProductId is { } selected)
        {
            var existing = (await catalog.GetAsync([selected], ct)).GetValueOrDefault(selected);
            if (existing is null)
                return Error.NotFound("product.not_found", "Product not found.");
            productId = selected;
            productAction = EntryProductAction.Selected;
            category = Enum.Parse<ProductCategory>(existing.Category);
        }
        else
        {
            var input = request.NewProduct!;
            CategoryParser.TryParse(input.Category, out category);
            QualityParser.TryParse(input.Quality, out var quality);
            string? ean = null;
            if (!string.IsNullOrWhiteSpace(input.Ean))
            {
                if (!Gtin.TryNormalize(input.Ean, out var normalizedEan))
                    return Error.Validation("product.invalid_ean", $"'{input.Ean}' is not a valid EAN/GTIN.");
                ean = normalizedEan;
            }
            var mpn = string.IsNullOrWhiteSpace(input.Mpn) ? null : input.Mpn.Trim();
            var key = new ProductKey(Text.NormalizeKey(input.Brand), mpn is null ? null : Text.NormalizeKey(mpn), ean, quality);
            var matcher = await catalog.CreateMatcherAsync([key], ct);
            var resolved = RowApplier.ResolveProduct(catalog, matcher, key,
                new NewProduct(input.Brand, input.Name, category, quality, mpn, ean, input.ModelName, input.CompatibleModels), OrganizationId);
            if (!resolved.IsSuccess)
                return resolved.Error!.Code == "product.identifier_conflict"
                    ? Error.Conflict("entry.identifier_conflict", resolved.Error.Message)
                    : resolved.Error;
            productId = resolved.Value.ProductId;
            productAction = resolved.Value.Created ? EntryProductAction.Created : EntryProductAction.MatchedExisting;
            if (!resolved.Value.Created)
                category = Enum.Parse<ProductCategory>((await catalog.GetAsync([productId], ct))[productId].Category);
        }

        var hasSerialized = productAction != EntryProductAction.Created && await inventory.HasSerializedStockAsync(warehouseId, productId, ct);
        if (ManualEntryRules.CheckStock(category, request.Quantity, imeis.Count, terms is not null, hasSerialized) is { } stockError)
            return stockError;

        // ---- stock ------------------------------------------------------------------------------------------------------
        var entryId = Guid.CreateVersion7();
        var reference = $"manual:{entryId}";
        var unitIds = new List<Guid>();
        foreach (var imei in imeis)
        {
            var unit = await inventory.StageReceiveUnitAsync(warehouseId, productId, imei, unitCost ?? 0,
                Inventory.Domain.TaxScheme.Standard, reference, ct);
            if (!unit.IsSuccess)
                return unit.Error!.Code == "unit.imei_in_stock"
                    ? Error.Conflict("unit.imei_in_stock", $"IMEI {imei} is already in stock. The same device cannot be stocked twice.")
                        .WithDetails(new Dictionary<string, object?> { ["imei"] = imei })
                    : unit.Error;
            unitIds.Add(unit.Value);
        }
        var quantityReceived = imeis.Count;
        if (imeis.Count == 0 && request.Quantity is > 0 and var quantity)
        {
            var received = await inventory.StageReceiveAsync(warehouseId, productId, quantity, unitCost, reference, ct);
            if (!received.IsSuccess)
                return received.Error!;
            quantityReceived = quantity;
        }

        // ---- offer (same upsert by SKU as the import) ---------------------------------------------------------------
        Guid? offerId = null;
        var offerAction = EntryOfferAction.None;
        if (terms is not null)
        {
            var existingOffer = (await offers.GetBySkuKeysAsync([Text.NormalizeKey(request.Offer!.SupplierSku)], ct)).Values.FirstOrDefault();
            if (existingOffer is not null && existingOffer.ProductId != productId)
                return Error.Conflict("entry.sku_conflict",
                    $"SKU {request.Offer.SupplierSku} is already linked to a different product. Archive that offer or use a new SKU.");
            var upsert = await RowApplier.StageOfferAsync(offers, request.Offer.SupplierSku, productId, warehouseId, terms, request.Offer.Publish, ct);
            if (!upsert.IsSuccess)
                return upsert.Error!;
            offerId = upsert.Value;
            offerAction = existingOffer is null ? EntryOfferAction.Created : EntryOfferAction.Updated;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            var entry = ManualEntry.Record(entryId, OrganizationId, idempotencyKey, requestHash, productId, productAction, warehouseId,
                quantityReceived, unitIds, unitCost, offerId, offerAction, currentUser.UserId, clock.UtcNow);
            var dto = await BuildResultAsync(entry, ct);
            entry.SetResult(JsonSerializer.Serialize(dto, Json));
            db.Add(entry);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Manual entry {EntryId}: product {ProductId} ({Action}), +{Quantity} in {Warehouse}, offer {OfferAction}",
                entryId, productId, productAction, quantityReceived, warehouseId, offerAction);
            return new ManualEntryResult(dto, Replayed: false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            // A concurrent request with the same key won: return its result (our changes rolled back).
            if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } concurrentReplay)
                return concurrentReplay;
            if (ex.IsUniqueViolation("ux_stock_units_imei_in_stock"))
                return Error.Conflict("unit.imei_in_stock", "One of the IMEIs was stocked concurrently. Nothing was saved.");
            if (ex.IsUniqueViolation("ix_offers_organization_id_sku_key"))
                return Error.Conflict("offer.sku_taken", "An offer with this SKU was created concurrently. Nothing was saved; try again.");
            logger.LogWarning(ex, "Manual entry {EntryId} hit a concurrent change", entryId);
            return Error.Conflict("entry.concurrent", "Data changed while the entry was saved. Nothing was saved; try again.");
        }
    }

    public async Task<LookupResultDto> LookupAsync(string? query, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        var isEan = Gtin.TryNormalize(q, out var ean);
        var looksLikeImei = !isEan && Imei.TryNormalize(q, out _);
        var matches = q.Length == 0 ? [] : await catalog.LookupAsync(q, LookupLimit, ct);
        var ids = matches.Select(m => m.Product.Id).ToList();
        var images = await catalog.GetImagesAsync(ids, ct);
        var stock = await inventory.GetTotalsAsync(ids, ct);
        var ownOffers = (await offers.GetOwnByProductsAsync(ids, ct)).ToLookup(o => o.ProductId);

        var dtos = matches.Select(m =>
        {
            var category = Enum.Parse<ProductCategory>(m.Product.Category);
            var productImages = images.GetValueOrDefault(m.Product.Id) ?? [];
            var total = stock.GetValueOrDefault(m.Product.Id);
            var own = ownOffers[m.Product.Id].Select(o => new LookupOwnOffer(o.Id, o.SupplierSku, o.PriceMinor, o.MinOrderQuantity,
                o.DeliveryDays, o.Condition.ToString(), o.Status.ToString())).ToList();
            return new LookupMatchDto(m.Product, m.MatchedBy, productImages.Count, productImages.FirstOrDefault()?.ThumbnailUrl,
                m.CreatedByCurrentOrganization && CanCreateProducts, SerializedStock.RequiresImei(category) || total?.SerializedUnitsInStock > 0,
                SerializedStock.AllowsImei(category), total?.OnHand ?? 0, total?.Available ?? 0, total?.SerializedUnitsInStock ?? 0, own,
                own.FirstOrDefault()?.SupplierSku ?? SuggestSku(m.Product));
        }).ToList();

        NewProductHint? hint = null;
        if (q.Length > 0 && !dtos.Any(d => d.MatchedBy == "Ean") && !looksLikeImei)
            hint = isEan ? new NewProductHint(ean, null) : q.Length <= 100 && !q.Contains(' ') ? new NewProductHint(null, q) : new NewProductHint(null, null);

        return new LookupResultDto(q, isEan, isEan ? ean : null, looksLikeImei, dtos, CanCreateProducts, CanCreateOffers, hint);
    }

    /// <summary>Default SKU for a new offer: the manufacturer part number, else the EAN (the dealer can change it).</summary>
    internal static string SuggestSku(ProductSummary product) =>
        product.Mpn is { Length: > 0 } mpn ? mpn : product.Ean ?? $"HN-{product.Id.ToString("N")[..10].ToUpperInvariant()}";

    private async Task<ManualEntryResultDto> BuildResultAsync(ManualEntry entry, CancellationToken ct)
    {
        var product = (await catalog.GetAsync([entry.ProductId], ct))[entry.ProductId];
        var level = (await inventory.GetLevelsAsync(entry.WarehouseId, [entry.ProductId], ct)).GetValueOrDefault(entry.ProductId);
        EntryOfferDto? offer = null;
        if (entry.OfferId is { } offerId && await offers.GetOwnAsync(offerId, ct) is { } o)
            offer = new EntryOfferDto(o.Id, entry.OfferAction.ToString(), o.SupplierSku, o.PriceMinor, Money.Eur, o.MinOrderQuantity,
                o.DeliveryDays, o.Condition.ToString(), o.Status.ToString());
        return new ManualEntryResultDto(entry.Id, entry.ProductAction.ToString(), product, entry.QuantityReceived, entry.UnitIds,
            entry.UnitCostMinor, new EntryStockDto(entry.WarehouseId, level?.OnHand ?? 0, level?.Reserved ?? 0,
                (level?.OnHand ?? 0) - (level?.Reserved ?? 0)), offer, entry.CreatedAt);
    }

    private async Task<Result<ManualEntryResult>?> ReplayAsync(string key, string requestHash, CancellationToken ct)
    {
        var existing = await db.Set<ManualEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.OrganizationId == OrganizationId && e.IdempotencyKey == key, ct);
        if (existing is null)
            return null;
        if (existing.RequestHash != requestHash)
            return Error.Validation("entry.idempotency_key_reused",
                    "This Idempotency-Key was already used for a different entry. Generate a new key for a new entry.")
                .WithDetails(new Dictionary<string, object?> { ["entryId"] = existing.Id });
        return new ManualEntryResult(JsonSerializer.Deserialize<ManualEntryResultDto>(existing.Result, Json)!, Replayed: true);
    }

    /// <summary>SHA-256 of the canonical request (IMEIs normalized, their order kept: it is the order of the unit ids).</summary>
    internal static string HashRequest(ManualEntryRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json))));
}
