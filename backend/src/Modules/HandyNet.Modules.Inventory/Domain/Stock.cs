using HandyNet.SharedKernel;

namespace HandyNet.Modules.Inventory.Domain;

public sealed class Warehouse : IAuditable
{
    private Warehouse() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsDefault { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Warehouse Create(Guid organizationId, string code, string name, bool isDefault, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        Code = Text.NormalizeKey(code),
        Name = name.Trim(),
        IsDefault = isDefault,
        CreatedAt = now,
    };
}

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum MovementType
{
    Receipt,
    Adjustment,
    ImportSync,
    Shipment,

    /// <summary>A serialized unit (IMEI) entered stock, e.g. bought from a private seller (Ankauf).</summary>
    UnitReceipt,

    /// <summary>Sold over the counter (POS). Never consumes reserved stock.</summary>
    Sale,

    /// <summary>Came back from a customer (POS return or cancelled receipt).</summary>
    Return,
}

/// <summary>
/// Quantity of one product in one warehouse. Invariants (also enforced by CHECK constraints):
/// on_hand ≥ 0, 0 ≤ reserved ≤ on_hand. Available = on_hand − reserved.
/// Every change to on_hand is accompanied by an append-only <see cref="StockMovement"/>.
/// </summary>
public sealed class StockLevel
{
    private StockLevel() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ProductId { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public int Available => OnHand - Reserved;

    public static StockLevel Create(Guid organizationId, Guid warehouseId, Guid productId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        WarehouseId = warehouseId,
        ProductId = productId,
        UpdatedAt = now,
    };

    public Result<StockMovement?> Adjust(int delta, MovementType type, string? reference, Guid? userId, DateTimeOffset now)
    {
        if (delta == 0)
            return Result<StockMovement?>.Success(null);
        if (OnHand + delta < 0)
            return Error.Conflict("stock.negative", $"On-hand quantity cannot become negative (currently {OnHand}).");
        if (OnHand + delta < Reserved)
            return Error.Conflict("stock.below_reserved",
                $"On-hand quantity cannot drop below the reserved quantity ({Reserved}).");

        OnHand += delta;
        UpdatedAt = now;
        return StockMovement.Record(this, delta, type, reference, userId, now);
    }

    public Result<StockMovement?> SetOnHand(int quantity, MovementType type, string? reference, Guid? userId, DateTimeOffset now) =>
        quantity < 0
            ? Error.Validation("stock.negative", "Quantity cannot be negative.")
            : Adjust(quantity - OnHand, type, reference, userId, now);

    /// <summary>Turns a reservation into an outgoing movement (goods leave the warehouse).</summary>
    public Result<StockMovement?> ConsumeReserved(int quantity, string reference, Guid? userId, DateTimeOffset now)
    {
        if (quantity <= 0 || quantity > Reserved || quantity > OnHand)
            return Error.Conflict("stock.reservation_mismatch", "Reserved quantity is inconsistent with the request.");
        Reserved -= quantity;
        OnHand -= quantity;
        UpdatedAt = now;
        return StockMovement.Record(this, -quantity, MovementType.Shipment, reference, userId, now);
    }
}

/// <summary>Append-only. UPDATE and DELETE are revoked from the application role.</summary>
public sealed class StockMovement
{
    private StockMovement() { }

    public long Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public int OnHandAfter { get; private set; }
    public MovementType Type { get; private set; }
    public string? Reference { get; private set; }
    public Guid? UserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    internal static StockMovement Record(StockLevel level, int delta, MovementType type, string? reference, Guid? userId, DateTimeOffset now) => new()
    {
        OrganizationId = level.OrganizationId,
        WarehouseId = level.WarehouseId,
        ProductId = level.ProductId,
        Quantity = delta,
        OnHandAfter = level.OnHand,
        Type = type,
        Reference = reference,
        UserId = userId,
        OccurredAt = now,
    };
}

public enum ReservationStatus
{
    Active,
    Released,
    Consumed,
}

/// <summary>
/// Stock held for a pending order. Created and released only through the database functions
/// <c>marketplace.reserve_offer_stock</c> / <c>inventory.release_reservation</c>, which update the
/// stock level in the same statement, so concurrent checkouts can never oversell.
/// </summary>
public sealed class StockReservation
{
    private StockReservation() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public ReservationStatus Status { get; private set; }
    public string Reference { get; private set; } = null!;
    public Guid ReservedByOrganizationId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public Result MarkConsumed(DateTimeOffset now)
    {
        if (Status != ReservationStatus.Active)
            return Error.Conflict("reservation.not_active", $"Reservation is {Status}.");
        Status = ReservationStatus.Consumed;
        ClosedAt = now;
        return Result.Success();
    }
}
