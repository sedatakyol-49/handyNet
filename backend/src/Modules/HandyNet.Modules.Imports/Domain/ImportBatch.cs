using HandyNet.SharedKernel;

namespace HandyNet.Modules.Imports.Domain;

public enum ImportFormat
{
    Csv,
    Xlsx,
}

public enum ImportStatus
{
    /// <summary>Parsed and validated; nothing applied yet (dry run).</summary>
    Previewed,
    Committed,
    Discarded,
}

public enum RowStatus
{
    Valid,
    Error,
    Conflict,
}

public enum ProductAction
{
    None,
    MatchExisting,
    CreateNew,
}

public enum OfferAction
{
    None,
    Create,
    Update,
    Unchanged,
}

/// <summary>Logical target fields an import column can be mapped to.</summary>
public static class ImportField
{
    public const string Sku = "sku";
    public const string Ean = "ean";
    public const string Mpn = "mpn";
    public const string Brand = "brand";
    public const string Name = "name";
    public const string Category = "category";
    public const string Quality = "quality";
    public const string Price = "price";
    public const string Quantity = "quantity";
    public const string DeliveryDays = "deliveryDays";
    public const string MinOrderQuantity = "minOrderQuantity";
    public const string Condition = "condition";
    public const string ModelName = "modelName";

    public static readonly string[] All =
        [Sku, Ean, Mpn, Brand, Name, Category, Quality, Price, Quantity, DeliveryDays, MinOrderQuantity, Condition, ModelName];

    public static readonly string[] Required = [Sku, Brand, Name, Price];
}

public sealed record ImportSummary(
    int TotalRows,
    int ValidRows,
    int ErrorRows,
    int ConflictRows,
    int NewProducts,
    int NewOffers,
    int UpdatedOffers,
    int UnchangedOffers,
    int StockChanges);

public sealed class ImportBatch : Entity, IAuditable
{
    private ImportBatch() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string FileName { get; private set; } = null!;
    public string FileSha256 { get; private set; } = null!;
    public ImportFormat Format { get; private set; }
    public Guid WarehouseId { get; private set; }
    public List<string> Headers { get; private set; } = [];
    public Dictionary<string, string> Mapping { get; private set; } = [];
    public bool PublishOffers { get; private set; }
    public bool SyncStock { get; private set; }

    /// <summary>Set at upload: the same file content had already been committed before.</summary>
    public bool DuplicateOfCommittedFile { get; private set; }
    public ImportStatus Status { get; private set; }
    public ImportSummary Summary { get; private set; } = null!;
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CompletedBy { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public uint Version { get; private set; }

    public static ImportBatch Create(Guid organizationId, string fileName, string sha256, ImportFormat format, Guid warehouseId,
        IReadOnlyList<string> headers, Dictionary<string, string> mapping, bool publishOffers, bool duplicateOfCommittedFile,
        ImportSummary summary, Guid userId,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        FileName = fileName,
        FileSha256 = sha256,
        Format = format,
        WarehouseId = warehouseId,
        Headers = headers.ToList(),
        Mapping = mapping,
        PublishOffers = publishOffers,
        DuplicateOfCommittedFile = duplicateOfCommittedFile,
        SyncStock = mapping.ContainsKey(ImportField.Quantity),
        Status = ImportStatus.Previewed,
        Summary = summary,
        CreatedBy = userId,
        CreatedAt = now,
    };

    public Result MarkCommitted(ImportSummary summary, Guid userId, DateTimeOffset now)
    {
        if (Status != ImportStatus.Previewed)
            return Error.Conflict("import.not_pending", $"Import is already {Status}.");
        Status = ImportStatus.Committed;
        Summary = summary;
        CompletedBy = userId;
        CompletedAt = now;
        return Result.Success();
    }

    public Result Discard(Guid userId, DateTimeOffset now)
    {
        if (Status != ImportStatus.Previewed)
            return Error.Conflict("import.not_pending", $"Import is already {Status}.");
        Status = ImportStatus.Discarded;
        CompletedBy = userId;
        CompletedAt = now;
        return Result.Success();
    }
}

public sealed class ImportRow
{
    private ImportRow() { }

    public long Id { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public int RowNumber { get; private set; }
    public Dictionary<string, string> Raw { get; private set; } = [];
    public RowStatus Status { get; private set; }
    public List<string> Messages { get; private set; } = [];

    public string? Sku { get; private set; }
    public string? Ean { get; private set; }
    public string? Mpn { get; private set; }
    public string? Brand { get; private set; }
    public string? Name { get; private set; }
    public string? Category { get; private set; }
    public string? Quality { get; private set; }
    public string? Condition { get; private set; }
    public string? ModelName { get; private set; }
    public long? PriceMinor { get; private set; }
    public int? Quantity { get; private set; }
    public int? DeliveryDays { get; private set; }
    public int? MinOrderQuantity { get; private set; }

    public ProductAction ProductAction { get; private set; }
    public Guid? ProductId { get; private set; }
    public OfferAction OfferAction { get; private set; }
    public Guid? OfferId { get; private set; }
    public long? OldPriceMinor { get; private set; }
    public int? OldQuantity { get; private set; }

    public static ImportRow From(Guid batchId, Guid organizationId, PlannedRow planned) => new()
    {
        BatchId = batchId,
        OrganizationId = organizationId,
        RowNumber = planned.Parsed.RowNumber,
        Raw = planned.Parsed.Raw,
        Status = planned.Status,
        Messages = planned.Messages.ToList(),
        Sku = planned.Parsed.Sku,
        Ean = planned.Parsed.Ean,
        Mpn = planned.Parsed.Mpn,
        Brand = planned.Parsed.Brand,
        Name = planned.Parsed.Name,
        Category = planned.Parsed.Category?.ToString(),
        Quality = planned.Parsed.Quality.ToString(),
        Condition = planned.Parsed.Condition.ToString(),
        ModelName = planned.Parsed.ModelName,
        PriceMinor = planned.Parsed.Price?.MinorUnits,
        Quantity = planned.Parsed.Quantity,
        DeliveryDays = planned.Parsed.DeliveryDays,
        MinOrderQuantity = planned.Parsed.MinOrderQuantity,
        ProductAction = planned.ProductAction,
        ProductId = planned.ProductId,
        OfferAction = planned.OfferAction,
        OfferId = planned.OfferId,
        OldPriceMinor = planned.OldPriceMinor,
        OldQuantity = planned.OldQuantity,
    };
}
