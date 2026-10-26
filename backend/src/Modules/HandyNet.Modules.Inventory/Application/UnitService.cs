using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Inventory.Application;

public sealed record UnitDto(
    Guid Id,
    string Imei,
    Guid ProductId,
    string Brand,
    string ProductName,
    string? Ean,
    Guid WarehouseId,
    string WarehouseCode,
    string Status,
    string TaxScheme,
    long PurchasePriceMinor,
    string Source,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ClosedAt);

/// <summary>One step in a unit's life, derived from its stock movements. StatusAfter: InStock or Sold.</summary>
public sealed record UnitHistoryEntryDto(long MovementId, string Type, int Quantity, string StatusAfter, string? Reference, Guid? UserId, DateTimeOffset OccurredAt);

public sealed record UnitDetailDto(UnitDto Unit, IReadOnlyList<UnitHistoryEntryDto> History);

/// <summary>
/// Read side of serialized stock (Geräte nach IMEI): the tenant's own units with search by IMEI prefix and each unit's
/// history from <c>stock_movements.unit_id</c> (receipt, sale, return). Devices enter stock only through
/// <see cref="Contracts.IInventory.StageReceiveUnitAsync"/> (Ankauf, Lager entry); this service never writes.
/// </summary>
internal sealed class UnitService(HandyNetDbContext db, ITenantContext tenant, IProductCatalog catalog)
{
    public const int MaxImeiLength = 15;

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<PagedResult<UnitDto>>> ListAsync(Guid? productId, UnitStatus? status, string? imeiPrefix, Guid? warehouseId,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockUnit>().AsNoTracking().Where(u => u.OrganizationId == OrganizationId);
        if (productId is { } pid)
            query = query.Where(u => u.ProductId == pid);
        if (status is { } s)
            query = query.Where(u => u.Status == s);
        if (warehouseId is { } w)
            query = query.Where(u => u.WarehouseId == w);
        if (!string.IsNullOrWhiteSpace(imeiPrefix))
        {
            var prefix = imeiPrefix.Trim();
            if (prefix.Length > MaxImeiLength || !prefix.All(char.IsAsciiDigit))
                return Error.Validation("unit.imei_prefix", "Search by the first digits of the IMEI (digits only, max 15).");
            query = query.Where(u => u.Imei.StartsWith(prefix));
        }

        var total = await query.CountAsync(ct);
        var units = await query.OrderByDescending(u => u.ReceivedAt).ThenBy(u => u.Imei).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<UnitDto>(await ToDtosAsync(units, ct), p, size, total);
    }

    public async Task<Result<UnitDetailDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var unit = await db.Set<StockUnit>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.OrganizationId == OrganizationId, ct);
        if (unit is null)
            return Error.NotFound("unit.not_found", "Device not found.");

        var movements = await db.Set<StockMovement>().AsNoTracking()
            .Where(m => m.OrganizationId == OrganizationId && m.UnitId == id)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        var history = movements.Select(m => new UnitHistoryEntryDto(m.Id, m.Type.ToString(), m.Quantity,
            m.Quantity > 0 ? nameof(UnitStatus.InStock) : nameof(UnitStatus.Sold), m.Reference, m.UserId, m.OccurredAt)).ToList();
        return new UnitDetailDto((await ToDtosAsync([unit], ct))[0], history);
    }

    private async Task<List<UnitDto>> ToDtosAsync(IReadOnlyList<StockUnit> units, CancellationToken ct)
    {
        var products = await catalog.GetAsync(units.Select(u => u.ProductId).Distinct().ToList(), ct);
        var warehouseIds = units.Select(u => u.WarehouseId).Distinct().ToList();
        var warehouses = await db.Set<Warehouse>().AsNoTracking()
            .Where(w => w.OrganizationId == OrganizationId && warehouseIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        return units.Select(u =>
        {
            var product = products.GetValueOrDefault(u.ProductId);
            return new UnitDto(u.Id, u.Imei, u.ProductId, product?.Brand ?? "", product?.Name ?? "", product?.Ean, u.WarehouseId,
                warehouses.GetValueOrDefault(u.WarehouseId) ?? "", u.Status.ToString(), u.TaxScheme.ToString(), u.PurchasePriceMinor, u.Source,
                u.ReceivedAt, u.ClosedAt);
        }).ToList();
    }
}
