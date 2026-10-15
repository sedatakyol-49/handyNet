using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ordering.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum OrderStatus
{
    Placed,
    Confirmed,
    Shipped,
    Cancelled,
    Rejected,

    /// <summary>Delivery confirmed (carrier event or buyer). The B2B inspection window runs from here.</summary>
    Delivered,

    /// <summary>Buyer accepted the goods, or the inspection window passed without a complaint.</summary>
    Completed,

    /// <summary>The buyer raised a complaint within the inspection window; completion and payout are on hold.</summary>
    Disputed,

    /// <summary>The dispute ended with a refund to the buyer (terminal).</summary>
    Refunded,
}

public sealed record OrderPlaced(Guid OrderId, Guid BuyerOrganizationId, Guid SellerOrganizationId, long GrossTotalMinor) : IDomainEvent;
public sealed record OrderStatusChanged(
    Guid OrderId, Guid BuyerOrganizationId, Guid SellerOrganizationId, string From, string To, string? Reason, DateTimeOffset At) : IDomainEvent;

/// <summary>
/// A B2B purchase from exactly one seller. Prices are snapshotted at placement and never change
/// afterwards; stock is reserved per line in the same transaction that creates the order.
/// Amounts are integer euro cents. VAT is calculated once per order on the net total
/// (single 19 % rate for now — see roadmap for reduced rates, §25a and reverse charge).
/// </summary>
public sealed class Order : Entity, IAuditable
{
    public const int StandardVatBasisPoints = 1900;

    private readonly List<OrderLine> _lines = [];

    private Order() { }

    public Guid Id { get; private set; }
    public long Number { get; private set; }
    public Guid BuyerOrganizationId { get; private set; }
    public Guid SellerOrganizationId { get; private set; }
    public OrderStatus Status { get; private set; }
    public string Currency { get; private set; } = null!;
    public long NetTotalMinor { get; private set; }
    public int VatRateBasisPoints { get; private set; }
    public long VatTotalMinor { get; private set; }
    public long GrossTotalMinor { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>SHA-256 of the normalized request, so a reused Idempotency-Key with a different body is detected.</summary>
    public string RequestHash { get; private set; } = null!;
    public string? BuyerReference { get; private set; }
    public Guid PlacedBy { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>End of the buyer's inspection period (delivery + configured window, default 48 h).</summary>
    public DateTimeOffset? InspectionEndsAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }
    public uint Version { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public string DisplayNumber => $"B2B-{Number:D6}";

    public static Result<Order> Place(Guid buyer, Guid seller, IReadOnlyList<NewOrderLine> lines, string idempotencyKey, string requestHash,
        string? buyerReference, Guid placedBy, DateTimeOffset now)
    {
        if (buyer == seller)
            return Error.Validation("order.self_purchase", "You cannot order from your own organization.");
        if (lines.Count == 0)
            return Error.Validation("order.empty", "An order needs at least one line.");
        if (lines.Select(l => l.OfferId).Distinct().Count() != lines.Count)
            return Error.Validation("order.duplicate_offer", "Each offer may appear only once per order.");
        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice.MinorUnits <= 0))
            return Error.Validation("order.invalid_line", "Quantities and prices must be positive.");
        if (lines.Select(l => l.UnitPrice.Currency).Distinct().Count() != 1)
            return Error.Validation("order.currency_mix", "All lines must use the same currency.");

        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            BuyerOrganizationId = buyer,
            SellerOrganizationId = seller,
            Status = OrderStatus.Placed,
            Currency = lines[0].UnitPrice.Currency,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            BuyerReference = string.IsNullOrWhiteSpace(buyerReference) ? null : buyerReference.Trim(),
            PlacedBy = placedBy,
            PlacedAt = now,
            VatRateBasisPoints = StandardVatBasisPoints,
        };

        var lineNumber = 0;
        var net = Money.Zero(order.Currency);
        foreach (var line in lines)
        {
            var lineNet = line.UnitPrice.Multiply(line.Quantity);
            net += lineNet;
            order._lines.Add(new OrderLine(order.Id, ++lineNumber, line, lineNet.MinorUnits));
        }

        var vat = net.ApplyRate(order.VatRateBasisPoints);
        order.NetTotalMinor = net.MinorUnits;
        order.VatTotalMinor = vat.MinorUnits;
        order.GrossTotalMinor = (net + vat).MinorUnits;
        order.Raise(new OrderPlaced(order.Id, buyer, seller, order.GrossTotalMinor));
        return order;
    }

    public void AttachReservation(int lineNumber, Guid reservationId) =>
        _lines.Single(l => l.LineNumber == lineNumber).AttachReservation(reservationId);

    public Result Confirm(DateTimeOffset now) =>
        Transition(OrderStatus.Placed, OrderStatus.Confirmed, now, () => ConfirmedAt = now);

    public Result MarkShipped(DateTimeOffset shippedAt) =>
        Transition(OrderStatus.Confirmed, OrderStatus.Shipped, shippedAt, () => ShippedAt = shippedAt);

    /// <summary>
    /// Records delivery. Idempotent for the same order so repeated carrier events are harmless.
    /// The inspection window starts at the actual delivery time, not when the event was processed.
    /// </summary>
    public Result MarkDelivered(DateTimeOffset deliveredAt, TimeSpan inspectionWindow, DateTimeOffset now)
    {
        if (Status == OrderStatus.Delivered || Status == OrderStatus.Completed)
            return Result.Success();
        if (deliveredAt > now.AddMinutes(5))
            return Error.Validation("order.delivery_in_future", "Delivery time cannot be in the future.");
        if (ShippedAt is { } shipped && deliveredAt < shipped)
            return Error.Validation("order.delivery_before_shipping", "Delivery cannot be before shipping.");
        return Transition(OrderStatus.Shipped, OrderStatus.Delivered, now, () =>
        {
            DeliveredAt = deliveredAt;
            InspectionEndsAt = deliveredAt + inspectionWindow;
        });
    }

    /// <summary>Buyer complains within the inspection window. Stops automatic completion (and therefore payout).</summary>
    public Result OpenDispute(DateTimeOffset now)
    {
        if (Status != OrderStatus.Delivered)
            return InvalidTransition(OrderStatus.Disputed);
        if (InspectionEndsAt is { } end && now >= end)
            return Error.Conflict("order.inspection_over", "The inspection period has ended; complaints are no longer possible here.");
        return Transition(OrderStatus.Delivered, OrderStatus.Disputed, now, () => { });
    }

    /// <summary>Dispute ended without refund (withdrawn by the buyer or decided for the seller): the order completes.</summary>
    public Result CloseDisputeWithoutRefund(DateTimeOffset now) =>
        Transition(OrderStatus.Disputed, OrderStatus.Completed, now, () => CompletedAt = now);

    /// <summary>Dispute ended with a full refund to the buyer.</summary>
    public Result CloseDisputeWithRefund(string reason, DateTimeOffset now) =>
        Transition(OrderStatus.Disputed, OrderStatus.Refunded, now, () => Close(reason, now));

    /// <summary>Buyer accepts the goods before the inspection window ends.</summary>
    public Result Accept(DateTimeOffset now) =>
        Transition(OrderStatus.Delivered, OrderStatus.Completed, now, () => CompletedAt = now);

    /// <summary>Closes the order automatically once the inspection window has passed.</summary>
    public Result CompleteAfterInspection(DateTimeOffset now)
    {
        if (Status != OrderStatus.Delivered)
            return InvalidTransition(OrderStatus.Completed);
        if (InspectionEndsAt is not { } end || now < end)
            return Error.Conflict("order.inspection_running", "The buyer's inspection period has not ended yet.");
        return Transition(OrderStatus.Delivered, OrderStatus.Completed, now, () => CompletedAt = now);
    }

    /// <summary>Buyer withdraws an order the seller has not confirmed yet.</summary>
    public Result Cancel(string? reason, DateTimeOffset now) =>
        Transition(OrderStatus.Placed, OrderStatus.Cancelled, now, () => Close(reason ?? "Cancelled by buyer", now));

    /// <summary>Seller declines an order before shipping it.</summary>
    public Result Reject(string reason, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Placed or OrderStatus.Confirmed))
            return InvalidTransition(OrderStatus.Rejected);
        var from = Status;
        Status = OrderStatus.Rejected;
        Close(reason, now);
        Raise(new OrderStatusChanged(Id, BuyerOrganizationId, SellerOrganizationId, from.ToString(), Status.ToString(), CloseReason, now));
        return Result.Success();
    }

    private void Close(string reason, DateTimeOffset now)
    {
        ClosedAt = now;
        CloseReason = reason.Trim();
    }

    private Result Transition(OrderStatus from, OrderStatus to, DateTimeOffset now, Action apply)
    {
        if (Status != from)
            return InvalidTransition(to);
        Status = to;
        apply();
        Raise(new OrderStatusChanged(Id, BuyerOrganizationId, SellerOrganizationId, from.ToString(), to.ToString(), CloseReason, now));
        return Result.Success();
    }

    private Error InvalidTransition(OrderStatus to) =>
        Error.Conflict("order.invalid_transition", $"An order in status {Status} cannot become {to}.");
}

public sealed record NewOrderLine(Guid OfferId, Guid ProductId, string SupplierSku, string Brand, string ProductName, int Quantity, Money UnitPrice);

public sealed class OrderLine
{
    private OrderLine() { }

    internal OrderLine(Guid orderId, int lineNumber, NewOrderLine line, long lineNetMinor)
    {
        Id = Guid.CreateVersion7();
        OrderId = orderId;
        LineNumber = lineNumber;
        OfferId = line.OfferId;
        ProductId = line.ProductId;
        SupplierSku = line.SupplierSku;
        Brand = line.Brand;
        ProductName = line.ProductName;
        Quantity = line.Quantity;
        UnitPriceMinor = line.UnitPrice.MinorUnits;
        LineNetMinor = lineNetMinor;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid OfferId { get; private set; }
    public Guid ProductId { get; private set; }
    public string SupplierSku { get; private set; } = null!;
    public string Brand { get; private set; } = null!;
    public string ProductName { get; private set; } = null!;
    public int Quantity { get; private set; }
    public long UnitPriceMinor { get; private set; }
    public long LineNetMinor { get; private set; }
    public Guid? ReservationId { get; private set; }

    internal void AttachReservation(Guid reservationId) => ReservationId = reservationId;
}
