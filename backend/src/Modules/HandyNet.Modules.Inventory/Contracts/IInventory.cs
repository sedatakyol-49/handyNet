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
}
