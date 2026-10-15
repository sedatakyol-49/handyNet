using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Shipping.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

public class ShippingRulesTests
{
    private static readonly Parcel Box = new(1000, 30, 20, 10);
    private static readonly CarrierProfile Profile = new();

    private static Error? Check(long value, bool insured = false, bool devices = false, bool batteries = false, Parcel? parcel = null) =>
        ShippingRules.Validate(Carrier.Dhl, Profile, new ShipmentCheck(parcel ?? Box, value, insured, devices, batteries));

    [Fact]
    public void Low_value_needs_no_insurance() => Assert.Null(Check(400_00));

    [Fact]
    public void Value_above_standard_liability_requires_insurance()
    {
        Assert.Equal("shipment.insurance_required", Check(600_00)!.Code);
        Assert.Null(Check(600_00, insured: true));
    }

    [Fact]
    public void Value_above_insurable_maximum_is_rejected() =>
        Assert.Equal("shipment.value_too_high", Check(3_000_00, insured: true)!.Code);

    [Fact]
    public void Loose_batteries_need_an_approved_carrier()
    {
        Assert.Equal("shipment.batteries_not_allowed", Check(100_00, batteries: true)!.Code);
        Assert.Null(ShippingRules.Validate(Carrier.Dhl, new CarrierProfile { AcceptsStandaloneLithiumBatteries = true },
            new ShipmentCheck(Box, 100_00, false, false, true)));
    }

    [Fact]
    public void Parcel_limits_are_enforced()
    {
        Assert.Equal("shipment.too_heavy", Check(100, parcel: new Parcel(40_000, 30, 20, 10))!.Code);
        Assert.Equal("shipment.too_long", Check(100, parcel: new Parcel(1000, 150, 20, 10))!.Code);
    }
}

public class ShipmentTrackingTests
{
    private static Shipment NewShipment() =>
        Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Carrier.Simulated, "T1", new Parcel(1, 1, 1, 1), 100, false, false, null,
            Guid.NewGuid(), DateTimeOffset.UnixEpoch);

    private static CarrierTrackingEvent Event(string id, int minutes, TrackingKind kind) =>
        new(id, DateTimeOffset.UnixEpoch.AddMinutes(minutes), kind, kind.ToString(), null);

    [Fact]
    public void Pickup_scan_counts_as_handover_and_delivery_completes()
    {
        var shipment = NewShipment();
        shipment.ApplyTracking([Event("a", 10, TrackingKind.PickedUp), Event("b", 20, TrackingKind.InTransit), Event("c", 30, TrackingKind.Delivered)],
            DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.Equal(ShipmentStatus.Delivered, shipment.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(10), shipment.HandedOverAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(30), shipment.DeliveredAt);
        Assert.Single(shipment.DomainEvents.OfType<ShipmentDelivered>());
    }

    [Fact]
    public void Duplicate_events_are_ignored()
    {
        var shipment = NewShipment();
        var events = new[] { Event("a", 10, TrackingKind.PickedUp) };
        Assert.Single(shipment.ApplyTracking(events, DateTimeOffset.UnixEpoch));
        Assert.Empty(shipment.ApplyTracking(events, DateTimeOffset.UnixEpoch));
        Assert.Single(shipment.Events);
    }

    [Fact]
    public void Delivery_without_handover_is_ignored_and_handed_over_cannot_be_cancelled()
    {
        var shipment = NewShipment();
        shipment.ApplyTracking([Event("x", 5, TrackingKind.Delivered)], DateTimeOffset.UnixEpoch);
        Assert.Equal(ShipmentStatus.LabelCreated, shipment.Status);

        shipment.HandOver(DateTimeOffset.UnixEpoch);
        Assert.False(shipment.Cancel().IsSuccess);
    }
}

public class OrderDeliveryTests
{
    private static Order ShippedOrder()
    {
        var line = new NewOrderLine(Guid.NewGuid(), Guid.NewGuid(), "S", "B", "N", 1, new Money(1000, Money.Eur));
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [line], "k", new string('0', 64), null, Guid.NewGuid(), DateTimeOffset.UnixEpoch).Value;
        order.Confirm(DateTimeOffset.UnixEpoch);
        order.MarkShipped(DateTimeOffset.UnixEpoch.AddHours(1));
        return order;
    }

    [Fact]
    public void Inspection_window_starts_at_actual_delivery_time()
    {
        var order = ShippedOrder();
        var deliveredAt = DateTimeOffset.UnixEpoch.AddHours(20);
        Assert.True(order.MarkDelivered(deliveredAt, TimeSpan.FromHours(48), DateTimeOffset.UnixEpoch.AddHours(30)).IsSuccess);
        Assert.Equal(deliveredAt.AddHours(48), order.InspectionEndsAt);
    }

    [Fact]
    public void Cannot_complete_before_the_window_ends()
    {
        var order = ShippedOrder();
        var at = DateTimeOffset.UnixEpoch.AddHours(2);
        order.MarkDelivered(at, TimeSpan.FromHours(48), at);
        Assert.Equal("order.inspection_running", order.CompleteAfterInspection(at.AddHours(47)).Error!.Code);
        Assert.True(order.CompleteAfterInspection(at.AddHours(48)).IsSuccess);
        Assert.Equal(OrderStatus.Completed, order.Status);
    }

    [Fact]
    public void Delivery_is_idempotent_and_validated()
    {
        var order = ShippedOrder();
        var now = DateTimeOffset.UnixEpoch.AddHours(5);
        Assert.False(order.MarkDelivered(DateTimeOffset.UnixEpoch, TimeSpan.FromHours(48), now).IsSuccess); // before shipping
        Assert.False(order.MarkDelivered(now.AddHours(1), TimeSpan.FromHours(48), now).IsSuccess);          // in the future
        Assert.True(order.MarkDelivered(now, TimeSpan.FromHours(48), now).IsSuccess);
        Assert.True(order.MarkDelivered(now, TimeSpan.FromHours(48), now).IsSuccess);                       // repeat is fine
    }
}
