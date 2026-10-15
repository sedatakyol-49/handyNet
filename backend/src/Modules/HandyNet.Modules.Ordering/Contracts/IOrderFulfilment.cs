using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ordering.Contracts;

public sealed record FulfilmentOrder(
    Guid Id,
    string Number,
    Guid BuyerOrganizationId,
    Guid SellerOrganizationId,
    string Status,
    long NetTotalMinor,
    long GrossTotalMinor,
    IReadOnlyList<Guid> ProductIds);

public sealed class OrderingOptions
{
    public const string Section = "Ordering";

    /// <summary>B2B inspection period after confirmed delivery (default 48 h). Subject to legal review; configurable.</summary>
    public int InspectionWindowHours { get; set; } = 48;
}

/// <summary>
/// Implemented by modules that attach fulfilment work to an order (Shipping). Ordering asks before shipping
/// without a tracked shipment or rejecting, so it never orphans a booked label — without depending on Shipping.
/// </summary>
public interface IShipmentGate
{
    Task<bool> HasOpenShipmentAsync(Guid orderId, CancellationToken ct);
}

/// <summary>
/// Used by Shipping and background jobs to move orders through fulfilment. Calls join the caller's
/// open transaction, so "shipment handed over" and "order shipped + stock consumed" commit together.
/// </summary>
public interface IOrderFulfilment
{
    /// <summary>The order if the current tenant is buyer or seller, otherwise null.</summary>
    Task<FulfilmentOrder?> GetForFulfilmentAsync(Guid orderId, CancellationToken ct);

    /// <summary>Seller only: Confirmed → Shipped at the carrier handover time, consuming the reserved stock.</summary>
    Task<Result> MarkShippedAsync(Guid orderId, DateTimeOffset shippedAt, CancellationToken ct);

    /// <summary>Shipped → Delivered at the given time; starts the inspection window. Idempotent.</summary>
    Task<Result> MarkDeliveredAsync(Guid orderId, DateTimeOffset deliveredAt, CancellationToken ct);

    Task<Result> CompleteAfterInspectionAsync(Guid orderId, CancellationToken ct);

    /// <summary>Buyer only: Delivered → Disputed (within the inspection window).</summary>
    Task<Result> OpenDisputeAsync(Guid orderId, CancellationToken ct);

    /// <summary>Disputed → Completed (no refund) or Refunded. Either party / system actor of the seller.</summary>
    Task<Result> CloseDisputeAsync(Guid orderId, bool refund, string reason, CancellationToken ct);
}
