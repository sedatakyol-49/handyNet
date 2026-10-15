using System.Text.Json;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Shipping.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Shipping.Application;

/// <summary>Tells Ordering whether a booked, not yet handed-over shipment exists (see <see cref="IShipmentGate"/>).</summary>
internal sealed class ShipmentGate(HandyNetDbContext db) : IShipmentGate
{
    public Task<bool> HasOpenShipmentAsync(Guid orderId, CancellationToken ct) =>
        db.Set<Shipment>().AnyAsync(s => s.OrderId == orderId && s.Status == ShipmentStatus.LabelCreated, ct);
}

/// <summary>
/// When the buyer confirms receipt on the order, shipments without carrier tracking (Manual) are marked delivered
/// too, so order and shipment never disagree. Runs as the seller (shipments are seller-written, ADR-0005).
/// </summary>
internal sealed class OrderDeliveredShipmentHandler(IServiceScopeFactory scopeFactory, HandyNetDbContext lookupDb) : IOutboxHandler
{
    public string EventType => typeof(OrderStatusChanged).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<OrderStatusChanged>(payload)!;
        if (e.To != nameof(OrderStatus.Delivered) || e.SellerOrganizationId == Guid.Empty)
            return;

        var ids = await lookupDb.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM shipping.shipments_for_order({e.OrderId})")
            .ToListAsync(ct);
        if (ids.Count == 0)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(e.SellerOrganizationId);
        var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
        var shipments = await db.Set<Shipment>().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        foreach (var shipment in shipments)
            shipment.MarkDeliveredByRecipient(e.At);
        await db.SaveChangesAsync(ct);
    }
}
