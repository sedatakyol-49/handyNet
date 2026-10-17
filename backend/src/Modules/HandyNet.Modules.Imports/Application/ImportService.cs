using System.Security.Cryptography;
using System.Text.Json;
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

namespace HandyNet.Modules.Imports.Application;

public sealed record ImportOptions(Guid? WarehouseId, bool PublishOffers = true, int DefaultDeliveryDays = 2,
    OfferCondition DefaultCondition = OfferCondition.New);

public sealed record ImportBatchDto(
    Guid Id,
    string FileName,
    string Format,
    string Status,
    Guid WarehouseId,
    bool PublishOffers,
    bool SyncStock,
    IReadOnlyDictionary<string, string> Mapping,
    IReadOnlyList<string> Headers,
    ImportSummary Summary,
    bool DuplicateOfCommittedFile,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ImportRowDto(
    int RowNumber,
    string Status,
    IReadOnlyList<string> Messages,
    string? Sku,
    string? Ean,
    string? Mpn,
    string? Brand,
    string? Name,
    string? Category,
    string? Quality,
    string? Condition,
    long? PriceMinor,
    long? OldPriceMinor,
    int? Quantity,
    int? OldQuantity,
    string ProductAction,
    Guid? ProductId,
    string OfferAction);

public sealed record CommitFailure(int RowNumber, string Message);

internal sealed class ImportService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IProductCatalog catalog,
    IOfferSync offers,
    IInventory inventory,
    IClock clock,
    ILogger<ImportService> logger)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<ImportBatchDto>> PreviewAsync(string fileName, byte[] content, Dictionary<string, string>? mapping,
        ImportOptions options, CancellationToken ct)
    {
        var format = TabularReaders.DetectFormat(fileName);
        if (!format.IsSuccess)
            return format.Error!;
        if (content.LongLength > TabularReaders.MaxFileBytes)
            return Error.Validation("import.too_large", "Files are limited to 10 MB.");

        var data = TabularReaders.Read(format.Value, content);
        if (!data.IsSuccess)
            return data.Error!;

        mapping ??= MappingGuesser.Guess(data.Value.Headers);
        if (ValidateMapping(mapping, data.Value.Headers) is { } mappingError)
            return mappingError;

        Guid warehouseId;
        if (options.WarehouseId is { } requested)
        {
            if (!await inventory.IsOwnWarehouseAsync(requested, ct))
                return Error.NotFound("warehouse.not_found", "Warehouse not found.");
            warehouseId = requested;
        }
        else
        {
            warehouseId = await inventory.GetOrStageDefaultWarehouseAsync(ct);
        }

        var defaults = new ParseDefaults(options.DefaultDeliveryDays, options.DefaultCondition);
        var parsed = data.Value.Rows.Select(r => RowParser.Parse(r.RowNumber, r.Values, mapping, defaults)).ToList();
        var planned = await PlanAsync(parsed, warehouseId, options.PublishOffers, ct);

        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var duplicate = await db.Set<ImportBatch>().AnyAsync(b => b.OrganizationId == OrganizationId
            && b.FileSha256 == sha && b.Status == ImportStatus.Committed, ct);
        var batch = ImportBatch.Create(OrganizationId, Path.GetFileName(fileName), sha, format.Value, warehouseId, data.Value.Headers, mapping,
            options.PublishOffers, duplicate, ImportPlanner.Summarize(planned), currentUser.UserId, clock.UtcNow);
        db.Add(batch);
        db.AddRange(planned.Select(p => ImportRow.From(batch.Id, OrganizationId, p)));
        await db.SaveChangesAsync(ct);

        return ToDto(batch);
    }

    public async Task<Result<ImportBatchDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var batch = await db.Set<ImportBatch>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.OrganizationId == OrganizationId, ct);
        return batch is null
            ? Error.NotFound("import.not_found", "Import not found.")
            : ToDto(batch);
    }

    public async Task<PagedResult<ImportBatchDto>> ListAsync(int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<ImportBatch>().AsNoTracking().Where(b => b.OrganizationId == OrganizationId);
        var total = await query.CountAsync(ct);
        var batches = await query.OrderByDescending(b => b.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var items = new List<ImportBatchDto>();
        foreach (var batch in batches)
            items.Add(ToDto(batch));
        return new PagedResult<ImportBatchDto>(items, p, size, total);
    }

    public async Task<Result<PagedResult<ImportRowDto>>> GetRowsAsync(Guid id, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await db.Set<ImportBatch>().AnyAsync(b => b.Id == id && b.OrganizationId == OrganizationId, ct))
            return Error.NotFound("import.not_found", "Import not found.");

        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<ImportRow>().AsNoTracking().Where(r => r.BatchId == id && r.OrganizationId == OrganizationId);
        if (Enum.TryParse<RowStatus>(status, true, out var parsed))
            query = query.Where(r => r.Status == parsed);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.RowNumber).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var items = rows.Select(r => new ImportRowDto(r.RowNumber, r.Status.ToString(), r.Messages, r.Sku, r.Ean, r.Mpn, r.Brand,
            r.Name, r.Category, r.Quality, r.Condition, r.PriceMinor, r.OldPriceMinor, r.Quantity, r.OldQuantity,
            r.ProductAction.ToString(), r.ProductId, r.OfferAction.ToString())).ToList();
        return new PagedResult<ImportRowDto>(items, p, size, total);
    }

    /// <summary>
    /// Applies all valid rows in one transaction: products, offers and stock either all change or
    /// none do. Rows are re-planned against current data first; if anything that was valid in the
    /// preview is no longer valid, nothing is applied and the reasons are returned.
    /// </summary>
    public async Task<Result<ImportBatchDto>> CommitAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Set<ImportBatch>().FirstOrDefaultAsync(b => b.Id == id && b.OrganizationId == OrganizationId, ct);
        if (batch is null)
            return Error.NotFound("import.not_found", "Import not found.");
        if (batch.Status != ImportStatus.Previewed)
            return Error.Conflict("import.not_pending", $"Import is already {batch.Status}.");

        var stored = await db.Set<ImportRow>().AsNoTracking()
            .Where(r => r.BatchId == id && r.OrganizationId == OrganizationId && r.Status == RowStatus.Valid)
            .OrderBy(r => r.RowNumber)
            .ToListAsync(ct);

        var parsed = stored.Select(ToParsed).ToList();
        var planned = await PlanAsync(parsed, batch.WarehouseId, batch.PublishOffers, ct);
        var failures = planned.Where(p => p.Status != RowStatus.Valid)
            .Select(p => new CommitFailure(p.Parsed.RowNumber, string.Join(" ", p.Messages)))
            .ToList();
        if (failures.Count > 0)
            return CommitRejected(failures);

        var matcher = await catalog.CreateMatcherAsync(parsed.Select(p => p.ProductKey).ToList(), ct);
        var reference = $"import:{batch.Id}";
        foreach (var row in planned)
        {
            var p = row.Parsed;
            var resolved = RowApplier.ResolveProduct(catalog, matcher, p.ProductKey,
                new NewProduct(p.Brand!, p.Name!, p.Category!.Value, p.Quality, p.Mpn, p.Ean, p.ModelName), OrganizationId);
            if (!resolved.IsSuccess)
                return CommitRejected([new CommitFailure(p.RowNumber, resolved.Error!.Message)]);
            var productId = resolved.Value.ProductId;

            var terms = new OfferTerms(p.Price!.Value, p.MinOrderQuantity, p.DeliveryDays, p.Condition);
            var upsert = await RowApplier.StageOfferAsync(offers, p.Sku!, productId, batch.WarehouseId, terms, batch.PublishOffers, ct);
            if (!upsert.IsSuccess)
                return CommitRejected([new CommitFailure(p.RowNumber, upsert.Error!.Message)]);

            if (p.Quantity is { } quantity)
            {
                var stock = await inventory.StageSetOnHandAsync(batch.WarehouseId, productId, quantity, reference, ct);
                if (!stock.IsSuccess)
                    return CommitRejected([new CommitFailure(p.RowNumber, stock.Error!.Message)]);
            }
        }

        var summary = ImportPlanner.Summarize(planned) with
        {
            TotalRows = batch.Summary.TotalRows,
            ErrorRows = batch.Summary.ErrorRows,
            ConflictRows = batch.Summary.ConflictRows,
        };
        var committed = batch.MarkCommitted(summary, currentUser.UserId, clock.UtcNow);
        if (!committed.IsSuccess)
            return committed.Error!;

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("import.concurrent", "Data changed while the import was applied. Nothing was imported; try again.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            logger.LogWarning(ex, "Import {BatchId} hit a unique constraint", batch.Id);
            return Error.Conflict("import.concurrent",
                "A product or offer was created concurrently. Nothing was imported; preview the file again.");
        }

        logger.LogInformation("Import {BatchId} committed: {@Summary}", batch.Id, summary);
        return ToDto(batch);
    }

    public async Task<Result<ImportBatchDto>> DiscardAsync(Guid id, CancellationToken ct)
    {
        var batch = await db.Set<ImportBatch>().FirstOrDefaultAsync(b => b.Id == id && b.OrganizationId == OrganizationId, ct);
        if (batch is null)
            return Error.NotFound("import.not_found", "Import not found.");
        var discarded = batch.Discard(currentUser.UserId, clock.UtcNow);
        if (!discarded.IsSuccess)
            return discarded.Error!;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("import.concurrent", "The import was changed concurrently.");
        }
        return ToDto(batch);
    }

    private async Task<IReadOnlyList<PlannedRow>> PlanAsync(IReadOnlyList<ParsedRow> parsed, Guid warehouseId, bool publish, CancellationToken ct)
    {
        var valid = parsed.Where(p => p.IsValid).ToList();
        var matcher = await catalog.CreateMatcherAsync(valid.Select(p => p.ProductKey).ToList(), ct);
        var existingOffers = await offers.GetBySkuKeysAsync(valid.Select(p => p.SkuKey).Distinct().ToList(), ct);

        var candidateProducts = valid
            .Select(p => matcher.Match(p.ProductKey))
            .Where(m => m.Kind == MatchKind.Existing)
            .Select(m => m.ProductId!.Value)
            .Distinct()
            .ToList();
        var stock = await inventory.GetLevelsAsync(warehouseId, candidateProducts, ct);

        return ImportPlanner.Plan(parsed, matcher, existingOffers, stock, publish);
    }

    private static Error? ValidateMapping(Dictionary<string, string> mapping, IReadOnlyList<string> headers)
    {
        var unknownField = mapping.Keys.FirstOrDefault(k => !ImportField.All.Contains(k));
        if (unknownField is not null)
            return Error.Validation("import.mapping_unknown_field", $"Unknown target field '{unknownField}'.");
        var unknownHeader = mapping.Values.FirstOrDefault(h => !headers.Contains(h));
        if (unknownHeader is not null)
            return Error.Validation("import.mapping_unknown_column", $"Column '{unknownHeader}' does not exist in the file.");
        var missing = ImportField.Required.Where(f => !mapping.ContainsKey(f)).ToList();
        if (missing.Count > 0)
            return Error.Validation("import.mapping_incomplete", $"Map these required fields: {string.Join(", ", missing)}.");
        if (!mapping.ContainsKey(ImportField.Ean) && !mapping.ContainsKey(ImportField.Mpn))
            return Error.Validation("import.mapping_incomplete", "Map an EAN or a manufacturer part number column.");
        return null;
    }

    private static ParsedRow ToParsed(ImportRow r) => new(
        r.RowNumber, r.Raw, r.Sku, r.Ean, r.Mpn, r.Brand, r.Name,
        Enum.Parse<ProductCategory>(r.Category!), Enum.Parse<PartQuality>(r.Quality!), Enum.Parse<OfferCondition>(r.Condition!),
        r.ModelName, new Money(r.PriceMinor!.Value, Money.Eur), r.Quantity, r.DeliveryDays!.Value, r.MinOrderQuantity!.Value, []);

    private static Error CommitRejected(IReadOnlyList<CommitFailure> failures) =>
        Error.Conflict("import.commit_rejected",
            "Nothing was imported because data changed since the preview: "
            + string.Join(" ", failures.Take(10).Select(f => $"Row {f.RowNumber}: {f.Message}"))
            + (failures.Count > 10 ? $" (+{failures.Count - 10} more)" : string.Empty));

    private static ImportBatchDto ToDto(ImportBatch batch) =>
        new(batch.Id, batch.FileName, batch.Format.ToString(), batch.Status.ToString(), batch.WarehouseId,
            batch.PublishOffers, batch.SyncStock, batch.Mapping, batch.Headers, batch.Summary, batch.DuplicateOfCommittedFile,
            batch.CreatedAt, batch.CompletedAt);

    public static Result<(Dictionary<string, string>? Mapping, ImportOptions Options)> ParseFormFields(string? mappingJson, string? optionsJson)
    {
        try
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            var mapping = string.IsNullOrWhiteSpace(mappingJson)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson, json)?
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            var options = string.IsNullOrWhiteSpace(optionsJson)
                ? new ImportOptions(null)
                : JsonSerializer.Deserialize<ImportOptions>(optionsJson, json) ?? new ImportOptions(null);
            if (options.DefaultDeliveryDays is < 0 or > Offer.MaxDeliveryDays)
                return Error.Validation("import.options", "Default delivery days out of range.");
            return (mapping, options);
        }
        catch (JsonException ex)
        {
            return Error.Validation("import.form", $"Invalid mapping or options JSON: {ex.Message}");
        }
    }
}
