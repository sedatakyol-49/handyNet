using HandyNet.SharedKernel;

namespace HandyNet.Modules.Inventory.Contracts;

public sealed record StockSnapshot(Guid WarehouseId, Guid ProductId, int OnHand, int Reserved);

/// <summary>Public API of the Inventory module. All operations act on the current tenant's stock.</summary>
public interface IInventory
{
    /// <summary>Returns the tenant's default warehouse, staging one if the tenant has none yet.</summary>
    Task<Guid> GetOrStageDefaultWarehouseAsync(CancellationToken ct);

    Task<bool> IsOwnWarehouseAsync(Guid warehouseId, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, StockSnapshot>> GetLevelsAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    /// <summary>Stages a stock correction to an absolute on-hand quantity, with a movement record.</summary>
    Task<Result> StageSetOnHandAsync(Guid warehouseId, Guid productId, int quantity, string reference, CancellationToken ct);

    /// <summary>Stages shipment of reserved stock: reservation → consumed, on-hand decreases, movement recorded.</summary>
    Task<Result> StageConsumeReservationAsync(Guid reservationId, string reference, CancellationToken ct);

    /// <summary>
    /// Releases an active reservation immediately (in the current transaction, if any). Allowed for the
    /// stock owner and for the organization that reserved it.
    /// </summary>
    Task<bool> ReleaseReservationAsync(Guid reservationId, CancellationToken ct);

    /// <summary>
    /// Stages receipt of one serialized device: a stock unit with its IMEI and purchase price, +1 on the product's
    /// stock level and a movement. Fails (on save) if the IMEI is already in stock anywhere on the platform.
    /// </summary>
    Task<Result<Guid>> StageReceiveUnitAsync(Guid warehouseId, Guid productId, string imei, long purchasePriceMinor,
        Domain.TaxScheme scheme, string source, CancellationToken ct);

    /// <summary>True if the IMEI is currently in stock (InStock or Reserved) at any organization.</summary>
    Task<bool> IsImeiInStockAnywhereAsync(string imei, CancellationToken ct);

    /// <summary>
    /// Stages an over-the-counter sale of a non-serialized product: on-hand decreases with a <c>Sale</c> movement.
    /// Same availability rule as B2B orders: reserved stock is never sold (available = on hand − reserved).
    /// Products with serialized units in the warehouse must be sold by IMEI. Concurrent changes fail on save (xmin).
    /// </summary>
    Task<Result> StageSellAsync(Guid warehouseId, Guid productId, int quantity, string reference, CancellationToken ct);

    /// <summary>Stages the sale of one own in-stock IMEI unit in the warehouse: unit → Sold, −1 on its stock level.</summary>
    Task<Result<UnitSnapshot>> StageSellUnitAsync(Guid warehouseId, string imei, string reference, CancellationToken ct);

    /// <summary>Stages a customer return of a non-serialized product: on-hand increases with a <c>Return</c> movement.</summary>
    Task<Result> StageReturnAsync(Guid warehouseId, Guid productId, int quantity, string reference, CancellationToken ct);

    /// <summary>
    /// Stages the return of a sold unit: Sold → InStock, +1 on its stock level. Saving fails if the same IMEI has been
    /// stocked anywhere on the platform in the meantime (unique index).
    /// </summary>
    Task<Result> StageReturnUnitAsync(Guid unitId, string reference, CancellationToken ct);

    /// <summary>The current tenant's in-stock (sellable) unit with this IMEI, or null. Never returns other tenants' units.</summary>
    Task<UnitSnapshot?> FindOwnUnitByImeiAsync(string imei, CancellationToken ct);

    /// <summary>True when the product has serialized units (InStock/Reserved) in the warehouse: sell by IMEI, not by quantity.</summary>
    Task<bool> HasSerializedStockAsync(Guid warehouseId, Guid productId, CancellationToken ct);
}

/// <summary>A serialized unit as other modules see it. The purchase price is the basis for §25a margin VAT.</summary>
public sealed record UnitSnapshot(
    Guid UnitId,
    Guid WarehouseId,
    Guid ProductId,
    string Imei,
    Domain.UnitStatus Status,
    Domain.TaxScheme TaxScheme,
    long PurchasePriceMinor);
