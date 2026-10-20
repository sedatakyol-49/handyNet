using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Shipping.Domain;
using HandyNet.Modules.Shipping.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Shipping.Application;

public sealed record CreateShipmentRequest(
    Guid OrderId,
    Carrier Carrier,
    int WeightGrams,
    int LengthCm,
    int WidthCm,
    int HeightCm,
    bool Insured,
    string? TrackingNumber);

public sealed record TrackingEventDto(DateTimeOffset OccurredAt, string Kind, string Description, string? Location);

public sealed record ShipmentDto(
    Guid Id,
    Guid OrderId,
    string Carrier,
    string Status,
    string TrackingNumber,
    int WeightGrams,
    long DeclaredValueMinor,
    bool Insured,
    bool ContainsLithiumBatteries,
    bool HasLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HandedOverAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<TrackingEventDto> Events);

internal sealed class CreateShipmentValidator : AbstractValidator<CreateShipmentRequest>
{
    public CreateShipmentValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Carrier).IsInEnum();
        RuleFor(x => x.WeightGrams).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.LengthCm).InclusiveBetween(1, 500);
        RuleFor(x => x.WidthCm).InclusiveBetween(1, 500);
        RuleFor(x => x.HeightCm).InclusiveBetween(1, 500);
        RuleFor(x => x.TrackingNumber).MaximumLength(100);
    }
}

internal sealed class ShipmentService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IOrderFulfilment orders,
    IProductCatalog catalog,
    IEnumerable<ICarrierAdapter> adapters,
    IOptions<ShippingOptions> options,
    IClock clock,
    ILogger<ShipmentService> logger)
{
    private static readonly ProductCategory[] DevicesWithBattery = [ProductCategory.Smartphone, ProductCategory.Tablet, ProductCategory.Wearable];

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public IReadOnlyList<string> AvailableCarriers() =>
        options.Value.EnabledCarriers.Where(c => adapters.Any(a => a.Carrier == c)).Select(c => c.ToString()).ToList();

    public async Task<Result<ShipmentDto>> CreateAsync(CreateShipmentRequest request, CancellationToken ct)
    {
        var order = await orders.GetForFulfilmentAsync(request.OrderId, ct);
        if (order is null || order.SellerOrganizationId != OrganizationId)
            return Error.NotFound("order.not_found", "Order not found.");
        if (order.Status != "Confirmed")
            return Error.Conflict("shipment.order_not_confirmed", "Only confirmed orders can be shipped.");
        if (await db.Set<Shipment>().AnyAsync(s => s.OrderId == order.Id && s.Status != ShipmentStatus.Cancelled, ct))
            return Error.Conflict("shipment.exists", "This order already has a shipment. Cancel it first to book a new one.");

        var adapter = adapters.FirstOrDefault(a => a.Carrier == request.Carrier);
        if (adapter is null || !options.Value.EnabledCarriers.Contains(request.Carrier))
            return Error.Validation("shipment.carrier_not_available", $"Carrier {request.Carrier} is not available.");

        var products = await catalog.GetAsync(order.ProductIds, ct);
        var categories = products.Values.Select(p => Enum.Parse<ProductCategory>(p.Category)).ToHashSet();
        var lithium = categories.Overlaps(DevicesWithBattery) || categories.Contains(ProductCategory.Battery);
        var parcel = new Parcel(request.WeightGrams, request.LengthCm, request.WidthCm, request.HeightCm);
        var check = new ShipmentCheck(parcel, order.GrossTotalMinor, request.Insured,
            categories.Overlaps(DevicesWithBattery), categories.Contains(ProductCategory.Battery));
        if (ShippingRules.Validate(request.Carrier, options.Value.ProfileFor(request.Carrier), check) is { } invalid)
            return invalid;

        var shipmentId = Guid.CreateVersion7();
        var label = await adapter.CreateLabelAsync(
            new LabelRequest(shipmentId, order.Number, parcel, request.Insured, order.GrossTotalMinor, request.TrackingNumber), ct);
        if (!label.IsSuccess)
            return label.Error!;

        var shipment = Shipment.Create(order.Id, order.SellerOrganizationId, order.BuyerOrganizationId, request.Carrier,
            label.Value.TrackingNumber, parcel, order.GrossTotalMinor, request.Insured, lithium, label.Value.LabelPdf,
            currentUser.UserId, clock.UtcNow);
        db.Add(shipment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            await adapter.CancelAsync(label.Value.TrackingNumber, ct);
            return Error.Conflict("shipment.tracking_taken", "This tracking number is already used by another shipment.");
        }

        return ToDto(shipment);
    }

    public async Task<IReadOnlyList<ShipmentDto>> ListForOrderAsync(Guid orderId, CancellationToken ct) =>
        (await db.Set<Shipment>().AsNoTracking().Include(s => s.Events)
            .Where(s => s.OrderId == orderId && (s.SellerOrganizationId == OrganizationId || s.BuyerOrganizationId == OrganizationId))
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<Result<byte[]>> GetLabelAsync(Guid id, CancellationToken ct)
    {
        var label = await db.Set<Shipment>().AsNoTracking()
            .Where(s => s.Id == id && s.SellerOrganizationId == OrganizationId)
            .Select(s => new { s.LabelPdf })
            .FirstOrDefaultAsync(ct);
        if (label is null)
            return Error.NotFound("shipment.not_found", "Shipment not found.");
        return label.LabelPdf is null
            ? Error.NotFound("shipment.no_label", "This shipment has no label (booked outside SEHA).")
            : label.LabelPdf;
    }

    /// <summary>Seller declares handover to the carrier: shipment HandedOver + order Shipped + stock consumed, atomically.</summary>
    public async Task<Result<ShipmentDto>> HandOverAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shipment = await LoadOwnAsync(id, ct);
        if (shipment is null)
            return Error.NotFound("shipment.not_found", "Shipment not found.");

        var handed = shipment.HandOver(clock.UtcNow);
        if (!handed.IsSuccess)
            return handed.Error!;
        var shipped = await orders.MarkShippedAsync(shipment.OrderId, shipment.HandedOverAt!.Value, ct);
        if (!shipped.IsSuccess)
            return shipped.Error!;

        return await SaveAsync(shipment, tx, ct);
    }

    public async Task<Result<ShipmentDto>> CancelAsync(Guid id, CancellationToken ct)
    {
        var shipment = await LoadOwnAsync(id, ct);
        if (shipment is null)
            return Error.NotFound("shipment.not_found", "Shipment not found.");
        var cancelled = shipment.Cancel();
        if (!cancelled.IsSuccess)
            return cancelled.Error!;

        var adapter = adapters.First(a => a.Carrier == shipment.Carrier);
        var carrierResult = await adapter.CancelAsync(shipment.TrackingNumber, ct);
        if (!carrierResult.IsSuccess)
            return carrierResult.Error!;
        return await SaveAsync(shipment, null, ct);
    }

    /// <summary>
    /// Pulls tracking from the carrier and applies it. A pickup scan ships the order; a delivery scan starts the
    /// buyer's inspection window. Runs for the seller (endpoint) or as the seller's system actor (worker).
    /// </summary>
    public async Task<Result<ShipmentDto>> SyncAsync(Guid id, CancellationToken ct)
    {
        var shipment = await LoadOwnAsync(id, ct);
        if (shipment is null)
            return Error.NotFound("shipment.not_found", "Shipment not found.");
        var adapter = adapters.First(a => a.Carrier == shipment.Carrier);
        var events = await adapter.GetTrackingAsync(shipment.TrackingNumber, shipment.HandedOverAt, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = shipment.Status;
        shipment.ApplyTracking(events, clock.UtcNow);

        if (before == ShipmentStatus.LabelCreated && shipment.HandedOverAt is not null)
        {
            var shipped = await orders.MarkShippedAsync(shipment.OrderId, shipment.HandedOverAt!.Value, ct);
            if (!shipped.IsSuccess)
                return shipped.Error!;
        }

        if (shipment.Status == ShipmentStatus.Delivered && before != ShipmentStatus.Delivered)
        {
            var delivered = await orders.MarkDeliveredAsync(shipment.OrderId, shipment.DeliveredAt!.Value, ct);
            if (!delivered.IsSuccess)
            {
                logger.LogWarning("Shipment {ShipmentId} delivered but order {OrderId} not updated: {Code}",
                    shipment.Id, shipment.OrderId, delivered.Error!.Code);
                return delivered.Error!;
            }
        }

        return await SaveAsync(shipment, tx, ct);
    }

    private Task<Shipment?> LoadOwnAsync(Guid id, CancellationToken ct) =>
        db.Set<Shipment>().Include(s => s.Events).FirstOrDefaultAsync(s => s.Id == id && s.SellerOrganizationId == OrganizationId, ct);

    private async Task<Result<ShipmentDto>> SaveAsync(Shipment shipment, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The shipment was changed concurrently. Reload and try again.");
        }
        return ToDto(shipment);
    }

    private static ShipmentDto ToDto(Shipment s) => new(
        s.Id, s.OrderId, s.Carrier.ToString(), s.Status.ToString(), s.TrackingNumber, s.WeightGrams, s.DeclaredValueMinor,
        s.Insured, s.ContainsLithiumBatteries, s.LabelPdf is not null, s.CreatedAt, s.HandedOverAt, s.DeliveredAt, s.LastSyncedAt,
        s.Events.OrderBy(e => e.OccurredAt).Select(e => new TrackingEventDto(e.OccurredAt, e.Kind.ToString(), e.Description, e.Location)).ToList());
}
