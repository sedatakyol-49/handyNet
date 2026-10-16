using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Inventory.Application;

public sealed record CreateWarehouseRequest(string Code, string Name);

/// <summary>Type: Receipt (goods received) or Adjustment (correction, count, damage). Defaults to Adjustment.</summary>
public sealed record AdjustStockRequest(Guid WarehouseId, Guid ProductId, int Delta, string Reason, MovementType? Type = null);

public sealed record WarehouseDto(Guid Id, string Code, string Name, bool IsDefault);

public sealed record StockRowDto(
    Guid WarehouseId,
    Guid ProductId,
    string Brand,
    string ProductName,
    string? Ean,
    string? Mpn,
    int OnHand,
    int Reserved,
    int Available,
    DateTimeOffset UpdatedAt);

public sealed record MovementDto(long Id, Guid WarehouseId, Guid ProductId, int Quantity, int OnHandAfter, string Type, string? Reference, DateTimeOffset OccurredAt);

internal sealed class CreateWarehouseValidator : AbstractValidator<CreateWarehouseRequest>
{
    public CreateWarehouseValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

internal sealed class AdjustStockValidator : AbstractValidator<AdjustStockRequest>
{
    public AdjustStockValidator()
    {
        RuleFor(x => x.Delta).NotEqual(0).InclusiveBetween(-1_000_000, 1_000_000);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Type).Must(t => t is null or MovementType.Receipt or MovementType.Adjustment)
            .WithMessage("Type must be Receipt or Adjustment.");
        RuleFor(x => x.Delta).GreaterThan(0).When(x => x.Type == MovementType.Receipt)
            .WithMessage("A receipt must increase stock.");
    }
}

internal sealed class InventoryService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IProductCatalog catalog,
    IClock clock) : IInventory
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;
    private Guid? UserId => currentUser.IsAuthenticated ? currentUser.UserId : null;

    public async Task<Guid> GetOrStageDefaultWarehouseAsync(CancellationToken ct)
    {
        var staged = db.ChangeTracker.Entries<Warehouse>()
            .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.OrganizationId == OrganizationId && e.Entity.IsDefault);
        if (staged is not null)
            return staged.Entity.Id;

        var existing = await db.Set<Warehouse>().Where(w => w.OrganizationId == OrganizationId && w.IsDefault)
            .Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id)
            return id;

        var warehouse = Warehouse.Create(OrganizationId, "MAIN", "Hauptlager", isDefault: true, clock.UtcNow);
        db.Add(warehouse);
        return warehouse.Id;
    }

    public Task<bool> IsOwnWarehouseAsync(Guid warehouseId, CancellationToken ct) =>
        db.Set<Warehouse>().AnyAsync(w => w.Id == warehouseId && w.OrganizationId == OrganizationId, ct);

    public async Task<IReadOnlyDictionary<Guid, StockSnapshot>> GetLevelsAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        await db.Set<StockLevel>().AsNoTracking()
            .Where(s => s.OrganizationId == OrganizationId && s.WarehouseId == warehouseId && productIds.Contains(s.ProductId))
            .Select(s => new StockSnapshot(s.WarehouseId, s.ProductId, s.OnHand, s.Reserved))
            .ToDictionaryAsync(s => s.ProductId, ct);

    public async Task<Result> StageSetOnHandAsync(Guid warehouseId, Guid productId, int quantity, string reference, CancellationToken ct)
    {
        var level = await GetOrStageLevelAsync(warehouseId, productId, ct);
        var result = level.SetOnHand(quantity, MovementType.ImportSync, reference, UserId, clock.UtcNow);
        if (!result.IsSuccess)
            return result.Error!;
        if (result.Value is { } movement)
            db.Add(movement);
        return Result.Success();
    }

    public async Task<Result> StageConsumeReservationAsync(Guid reservationId, string reference, CancellationToken ct)
    {
        var reservation = await db.Set<StockReservation>()
            .FirstOrDefaultAsync(r => r.Id == reservationId && r.OrganizationId == OrganizationId, ct);
        if (reservation is null)
            return Error.NotFound("reservation.not_found", "Reservation not found.");

        var level = await db.Set<StockLevel>()
            .FirstAsync(s => s.WarehouseId == reservation.WarehouseId && s.ProductId == reservation.ProductId, ct);

        var marked = reservation.MarkConsumed(clock.UtcNow);
        if (!marked.IsSuccess)
            return marked;

        var consumed = level.ConsumeReserved(reservation.Quantity, reference, UserId, clock.UtcNow);
        if (!consumed.IsSuccess)
            return consumed.Error!;
        db.Add(consumed.Value!);
        return Result.Success();
    }

    public async Task<bool> ReleaseReservationAsync(Guid reservationId, CancellationToken ct)
    {
        var released = await db.Database
            .SqlQuery<bool>($"SELECT inventory.release_reservation({reservationId}) AS \"Value\"")
            .SingleAsync(ct);
        return released;
    }

    public async Task<Result<Guid>> StageReceiveUnitAsync(Guid warehouseId, Guid productId, string imei, long purchasePriceMinor,
        TaxScheme scheme, string source, CancellationToken ct)
    {
        if (!await IsOwnWarehouseAsync(warehouseId, ct) && !db.ChangeTracker.Entries<Warehouse>().Any(e => e.Entity.Id == warehouseId))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");
        if (await IsImeiInStockAnywhereAsync(imei, ct))
            return Error.Conflict("unit.imei_in_stock", "This IMEI is already in stock. The same device cannot be stocked twice.");

        var unit = StockUnit.Receive(OrganizationId, warehouseId, productId, imei, purchasePriceMinor, scheme, source, clock.UtcNow);
        if (!unit.IsSuccess)
            return unit.Error!;
        var level = await GetOrStageLevelAsync(warehouseId, productId, ct);
        var movement = level.Adjust(1, MovementType.UnitReceipt, source, UserId, clock.UtcNow);
        if (!movement.IsSuccess)
            return movement.Error!;
        db.Add(unit.Value);
        db.Add(movement.Value!);
        return unit.Value.Id;
    }

    public Task<bool> HasSerializedStockAsync(Guid warehouseId, Guid productId, CancellationToken ct) =>
        db.Set<StockUnit>().AnyAsync(u => u.OrganizationId == OrganizationId && u.WarehouseId == warehouseId && u.ProductId == productId
                                          && (u.Status == UnitStatus.InStock || u.Status == UnitStatus.Reserved), ct);

    /// <summary>Checked through an id-free SECURITY DEFINER function: units of other organizations stay invisible.</summary>
    public async Task<bool> IsImeiInStockAnywhereAsync(string imei, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"SELECT inventory.imei_in_stock({imei}) AS \"Value\"").SingleAsync(ct);

    public async Task<Result> StageSellAsync(Guid warehouseId, Guid productId, int quantity, string reference, CancellationToken ct)
    {
        if (quantity <= 0)
            return Error.Validation("stock.quantity", "Quantity must be positive.");
        if (await HasSerializedStockAsync(warehouseId, productId, ct))
            return Error.Validation("stock.serialized_requires_imei", "This product is stocked as serialized devices. Scan the IMEI of the device sold.")
                .WithDetails(new Dictionary<string, object?> { ["productId"] = productId });

        var level = await FindLevelAsync(warehouseId, productId, ct);
        var available = level?.Available ?? 0;
        if (level is null || available < quantity)
            return Insufficient(productId, quantity, available);

        var result = level.Adjust(-quantity, MovementType.Sale, reference, UserId, clock.UtcNow);
        if (!result.IsSuccess)
            return Insufficient(productId, quantity, available);
        db.Add(result.Value!);
        return Result.Success();
    }

    public async Task<Result<UnitSnapshot>> StageSellUnitAsync(Guid warehouseId, string imei, string reference, CancellationToken ct)
    {
        var unit = await db.Set<StockUnit>()
            .FirstOrDefaultAsync(u => u.OrganizationId == OrganizationId && u.Imei == imei && u.Status == UnitStatus.InStock, ct);
        if (unit is null)
            return Error.Conflict("unit.not_available", $"No device with IMEI {imei} is in stock.");
        if (unit.WarehouseId != warehouseId)
            return Error.Conflict("unit.other_warehouse", $"The device with IMEI {imei} is stocked in another warehouse.");

        var sold = unit.MarkSold(clock.UtcNow);
        if (!sold.IsSuccess)
            return sold.Error!;
        var level = await FindLevelAsync(unit.WarehouseId, unit.ProductId, ct);
        if (level is null)
            return Error.Conflict("stock.inconsistent", "The device has no stock level. Correct the stock first.");
        var movement = level.Adjust(-1, MovementType.Sale, reference, UserId, clock.UtcNow);
        if (!movement.IsSuccess)
            return movement.Error!; // e.g. the last unit's quantity is reserved by an order
        db.Add(movement.Value!);
        return ToSnapshot(unit);
    }

    public async Task<Result> StageReturnAsync(Guid warehouseId, Guid productId, int quantity, string reference, CancellationToken ct)
    {
        if (quantity <= 0)
            return Error.Validation("stock.quantity", "Quantity must be positive.");
        if (!await IsOwnWarehouseAsync(warehouseId, ct))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");
        var level = await GetOrStageLevelAsync(warehouseId, productId, ct);
        var movement = level.Adjust(quantity, MovementType.Return, reference, UserId, clock.UtcNow);
        if (!movement.IsSuccess)
            return movement.Error!;
        db.Add(movement.Value!);
        return Result.Success();
    }

    public async Task<Result> StageReturnUnitAsync(Guid unitId, string reference, CancellationToken ct)
    {
        var unit = await db.Set<StockUnit>().FirstOrDefaultAsync(u => u.Id == unitId && u.OrganizationId == OrganizationId, ct);
        if (unit is null)
            return Error.NotFound("unit.not_found", "Device not found.");
        var returned = unit.ReturnToStock(clock.UtcNow);
        if (!returned.IsSuccess)
            return returned;
        var level = await GetOrStageLevelAsync(unit.WarehouseId, unit.ProductId, ct);
        var movement = level.Adjust(1, MovementType.Return, reference, UserId, clock.UtcNow);
        if (!movement.IsSuccess)
            return movement.Error!;
        db.Add(movement.Value!);
        return Result.Success();
    }

    public async Task<UnitSnapshot?> FindOwnUnitByImeiAsync(string imei, CancellationToken ct)
    {
        var unit = await db.Set<StockUnit>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.OrganizationId == OrganizationId && u.Imei == imei && u.Status == UnitStatus.InStock, ct);
        return unit is null ? null : ToSnapshot(unit);
    }

    private static UnitSnapshot ToSnapshot(StockUnit u) =>
        new(u.Id, u.WarehouseId, u.ProductId, u.Imei, u.Status, u.TaxScheme, u.PurchasePriceMinor);

    private static Error Insufficient(Guid productId, int requested, int available) =>
        Error.Conflict("stock.insufficient", $"Not enough stock available (requested {requested}, available {available}). Reserved stock cannot be sold.")
            .WithDetails(new Dictionary<string, object?>
            {
                ["productId"] = productId,
                ["requestedQuantity"] = requested,
                ["availableQuantity"] = available,
            });

    private async Task<StockLevel?> FindLevelAsync(Guid warehouseId, Guid productId, CancellationToken ct) =>
        db.ChangeTracker.Entries<StockLevel>().FirstOrDefault(e => e.Entity.WarehouseId == warehouseId && e.Entity.ProductId == productId)?.Entity
        ?? await db.Set<StockLevel>()
            .FirstOrDefaultAsync(s => s.OrganizationId == OrganizationId && s.WarehouseId == warehouseId && s.ProductId == productId, ct);

    // ---- endpoint operations -------------------------------------------------------------------

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken ct) =>
        await db.Set<Warehouse>().AsNoTracking()
            .Where(w => w.OrganizationId == OrganizationId)
            .OrderByDescending(w => w.IsDefault).ThenBy(w => w.Code)
            .Select(w => new WarehouseDto(w.Id, w.Code, w.Name, w.IsDefault))
            .ToListAsync(ct);

    public async Task<Result<WarehouseDto>> CreateWarehouseAsync(CreateWarehouseRequest request, CancellationToken ct)
    {
        var hasDefault = await db.Set<Warehouse>().AnyAsync(w => w.OrganizationId == OrganizationId && w.IsDefault, ct);
        var warehouse = Warehouse.Create(OrganizationId, request.Code, request.Name, isDefault: !hasDefault, clock.UtcNow);
        db.Add(warehouse);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("warehouse.code_taken", "A warehouse with this code already exists.");
        }

        return new WarehouseDto(warehouse.Id, warehouse.Code, warehouse.Name, warehouse.IsDefault);
    }

    public async Task<PagedResult<StockRowDto>> ListStockAsync(Guid? warehouseId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockLevel>().AsNoTracking().Where(s => s.OrganizationId == OrganizationId);
        if (warehouseId is { } w)
            query = query.Where(s => s.WarehouseId == w);

        var total = await query.CountAsync(ct);
        var levels = await query.OrderByDescending(s => s.UpdatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var products = await catalog.GetAsync(levels.Select(l => l.ProductId).Distinct().ToList(), ct);

        var rows = levels.Select(l =>
        {
            var product = products[l.ProductId];
            return new StockRowDto(l.WarehouseId, l.ProductId, product.Brand, product.Name, product.Ean, product.Mpn,
                l.OnHand, l.Reserved, l.Available, l.UpdatedAt);
        }).ToList();
        return new PagedResult<StockRowDto>(rows, p, size, total);
    }

    public async Task<Result<StockRowDto>> AdjustAsync(AdjustStockRequest request, CancellationToken ct)
    {
        if (!await IsOwnWarehouseAsync(request.WarehouseId, ct))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");
        var products = await catalog.GetAsync([request.ProductId], ct);
        if (!products.TryGetValue(request.ProductId, out var product))
            return Error.NotFound("product.not_found", "Product not found.");

        var level = await GetOrStageLevelAsync(request.WarehouseId, request.ProductId, ct);
        var type = request.Type ?? MovementType.Adjustment;
        var result = level.Adjust(request.Delta, type, request.Reason.Trim(), UserId, clock.UtcNow);
        if (!result.IsSuccess)
            return result.Error!;
        db.Add(result.Value!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            return Error.Conflict("concurrency", "Stock was changed concurrently. Reload and try again.");
        }

        return new StockRowDto(level.WarehouseId, level.ProductId, product.Brand, product.Name, product.Ean, product.Mpn,
            level.OnHand, level.Reserved, level.Available, level.UpdatedAt);
    }

    public async Task<PagedResult<MovementDto>> ListMovementsAsync(Guid? productId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockMovement>().AsNoTracking().Where(m => m.OrganizationId == OrganizationId);
        if (productId is { } id)
            query = query.Where(m => m.ProductId == id);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.Id).Skip((p - 1) * size).Take(size)
            .Select(m => new MovementDto(m.Id, m.WarehouseId, m.ProductId, m.Quantity, m.OnHandAfter, m.Type.ToString(), m.Reference, m.OccurredAt))
            .ToListAsync(ct);
        return new PagedResult<MovementDto>(items, p, size, total);
    }

    private async Task<StockLevel> GetOrStageLevelAsync(Guid warehouseId, Guid productId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<StockLevel>()
            .FirstOrDefault(e => e.Entity.WarehouseId == warehouseId && e.Entity.ProductId == productId);
        if (tracked is not null)
            return tracked.Entity;

        var level = await db.Set<StockLevel>()
            .FirstOrDefaultAsync(s => s.OrganizationId == OrganizationId && s.WarehouseId == warehouseId && s.ProductId == productId, ct);
        if (level is null)
        {
            level = StockLevel.Create(OrganizationId, warehouseId, productId, clock.UtcNow);
            db.Add(level);
        }

        return level;
    }
}
