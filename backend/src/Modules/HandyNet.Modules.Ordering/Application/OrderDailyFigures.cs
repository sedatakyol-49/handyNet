using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ordering.Application;

internal sealed class OrderDailyFigures(HandyNetDbContext db, ITenantContext tenant) : IOrderDailyFigures
{
    public async Task<OrderDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var org = tenant.RequiredOrganizationId;
        var f = from.ToUniversalTime();
        var u = until.ToUniversalTime();
        var orders = await db.Set<Order>().AsNoTracking()
            .Where(o => (o.BuyerOrganizationId == org || o.SellerOrganizationId == org)
                        && ((o.PlacedAt >= f && o.PlacedAt < u) || (o.DeliveredAt >= f && o.DeliveredAt < u) || (o.ShippedAt >= f && o.ShippedAt < u)
                            || (o.CompletedAt >= f && o.CompletedAt < u)))
            .ToListAsync(ct);

        bool In(DateTimeOffset? at) => at is { } t && t >= f && t < u;
        List<OrderDayEvent> Events(Func<Order, bool> party, Func<Order, DateTimeOffset?> at, string name) =>
            orders.Where(o => party(o) && In(at(o))).OrderBy(o => at(o))
                .Select(o => new OrderDayEvent(o.Id, o.DisplayNumber, name, o.Status.ToString(), at(o)!.Value, o.GrossTotalMinor)).ToList();

        return new OrderDayFigures(
            Events(o => o.BuyerOrganizationId == org, o => o.PlacedAt, "Placed"),
            Events(o => o.BuyerOrganizationId == org, o => o.DeliveredAt, "Delivered"),
            Events(o => o.SellerOrganizationId == org, o => o.PlacedAt, "Placed"),
            Events(o => o.SellerOrganizationId == org, o => o.ShippedAt, "Shipped"),
            Events(o => o.SellerOrganizationId == org, o => o.CompletedAt, "Completed"));
    }
}
