using HandyNet.SharedKernel;

namespace HandyNet.Modules.Shipping.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum Carrier
{
    /// <summary>Development/demo adapter. Produces clearly marked fake labels and simulated tracking.</summary>
    Simulated,

    /// <summary>Seller books the parcel elsewhere and enters the tracking number; no label, no tracking sync.</summary>
    Manual,
    Dhl,
    Dpd,
    Gls,
    Ups,
}

public enum ShipmentStatus
{
    /// <summary>Booked with the carrier, label available. Does NOT mean the parcel left the seller.</summary>
    LabelCreated,
    HandedOver,
    InTransit,
    Delivered,
    Exception,
    Cancelled,
}

public sealed record Parcel(int WeightGrams, int LengthCm, int WidthCm, int HeightCm);

public sealed record ShipmentDelivered(Guid ShipmentId, Guid OrderId, DateTimeOffset DeliveredAt) : IDomainEvent;

/// <summary>A parcel for one B2B order, owned by the seller and visible to the buyer.</summary>
public sealed class Shipment : Entity, IAuditable
{
    private readonly List<TrackingEvent> _events = [];

    private Shipment() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid SellerOrganizationId { get; private set; }
    public Guid BuyerOrganizationId { get; private set; }
    public Carrier Carrier { get; private set; }
    public ShipmentStatus Status { get; private set; }
    public string TrackingNumber { get; private set; } = null!;
    public int WeightGrams { get; private set; }
    public int LengthCm { get; private set; }
    public int WidthCm { get; private set; }
    public int HeightCm { get; private set; }
    public long DeclaredValueMinor { get; private set; }
    public bool Insured { get; private set; }
    public bool ContainsLithiumBatteries { get; private set; }
    public byte[]? LabelPdf { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? HandedOverAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public uint Version { get; private set; }

    public IReadOnlyList<TrackingEvent> Events => _events;

    public static Shipment Create(Guid orderId, Guid seller, Guid buyer, Carrier carrier, string trackingNumber, Parcel parcel,
        long declaredValueMinor, bool insured, bool lithium, byte[]? label, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        SellerOrganizationId = seller,
        BuyerOrganizationId = buyer,
        Carrier = carrier,
        Status = ShipmentStatus.LabelCreated,
        TrackingNumber = trackingNumber.Trim(),
        WeightGrams = parcel.WeightGrams,
        LengthCm = parcel.LengthCm,
        WidthCm = parcel.WidthCm,
        HeightCm = parcel.HeightCm,
        DeclaredValueMinor = declaredValueMinor,
        Insured = insured,
        ContainsLithiumBatteries = lithium,
        LabelPdf = label,
        CreatedAt = now,
        CreatedBy = userId,
    };

    public bool IsActive => Status is ShipmentStatus.LabelCreated or ShipmentStatus.HandedOver or ShipmentStatus.InTransit or ShipmentStatus.Exception;

    /// <summary>The seller declares the parcel was handed to the carrier (or a carrier pickup scan arrived).</summary>
    public Result HandOver(DateTimeOffset at)
    {
        if (Status != ShipmentStatus.LabelCreated)
            return Error.Conflict("shipment.invalid_status", $"A shipment in status {Status} cannot be handed over.");
        Status = ShipmentStatus.HandedOver;
        HandedOverAt = at;
        return Result.Success();
    }

    /// <summary>
    /// The buyer confirmed receipt on the order (typical for Manual shipments without tracking). Does not raise
    /// <see cref="ShipmentDelivered"/> — the order is already delivered.
    /// </summary>
    public bool MarkDeliveredByRecipient(DateTimeOffset at)
    {
        if (Status is not (ShipmentStatus.HandedOver or ShipmentStatus.InTransit or ShipmentStatus.Exception))
            return false;
        Status = ShipmentStatus.Delivered;
        DeliveredAt = at;
        return true;
    }

    public Result Cancel()
    {
        if (Status != ShipmentStatus.LabelCreated)
            return Error.Conflict("shipment.invalid_status", "Only shipments that were not handed over can be cancelled.");
        Status = ShipmentStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>
    /// Applies carrier events in time order. Duplicates (same external id) are ignored, so polling the
    /// carrier repeatedly is safe. Returns the events that were new.
    /// </summary>
    public IReadOnlyList<TrackingEvent> ApplyTracking(IEnumerable<CarrierTrackingEvent> incoming, DateTimeOffset now)
    {
        LastSyncedAt = now;
        var added = new List<TrackingEvent>();
        foreach (var e in incoming.OrderBy(e => e.OccurredAt))
        {
            if (_events.Any(x => x.ExternalId == e.ExternalId))
                continue;
            var tracking = new TrackingEvent(Id, e, now);
            _events.Add(tracking);
            added.Add(tracking);

            switch (e.Kind)
            {
                case TrackingKind.PickedUp when Status == ShipmentStatus.LabelCreated:
                    Status = ShipmentStatus.HandedOver;
                    HandedOverAt = e.OccurredAt;
                    break;
                case TrackingKind.InTransit when Status is ShipmentStatus.HandedOver or ShipmentStatus.Exception:
                    Status = ShipmentStatus.InTransit;
                    break;
                case TrackingKind.Exception when Status is ShipmentStatus.HandedOver or ShipmentStatus.InTransit:
                    Status = ShipmentStatus.Exception;
                    break;
                case TrackingKind.Delivered when Status is ShipmentStatus.HandedOver or ShipmentStatus.InTransit or ShipmentStatus.Exception:
                    Status = ShipmentStatus.Delivered;
                    DeliveredAt = e.OccurredAt;
                    Raise(new ShipmentDelivered(Id, OrderId, e.OccurredAt));
                    break;
            }
        }

        return added;
    }
}

public enum TrackingKind
{
    Info,
    PickedUp,
    InTransit,
    Exception,
    Delivered,
}

public sealed record CarrierTrackingEvent(string ExternalId, DateTimeOffset OccurredAt, TrackingKind Kind, string Description, string? Location);

public sealed class TrackingEvent
{
    private TrackingEvent() { }

    internal TrackingEvent(Guid shipmentId, CarrierTrackingEvent e, DateTimeOffset receivedAt)
    {
        Id = Guid.CreateVersion7();
        ShipmentId = shipmentId;
        ExternalId = e.ExternalId;
        OccurredAt = e.OccurredAt;
        Kind = e.Kind;
        Description = e.Description;
        Location = e.Location;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }
    public Guid ShipmentId { get; private set; }
    public string ExternalId { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public TrackingKind Kind { get; private set; }
    public string Description { get; private set; } = null!;
    public string? Location { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
}
