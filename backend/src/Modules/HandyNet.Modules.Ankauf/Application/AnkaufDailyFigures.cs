using HandyNet.Modules.Ankauf.Contracts;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ankauf.Application;

internal sealed class AnkaufDailyFigures(HandyNetDbContext db, ITenantContext tenant) : IAnkaufDailyFigures
{
    public async Task<AnkaufDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var org = tenant.RequiredOrganizationId;
        var f = from.ToUniversalTime();
        var u = until.ToUniversalTime();
        var received = await db.Set<Intake>().AsNoTracking().CountAsync(i => i.OrganizationId == org && i.ReceivedAt >= f && i.ReceivedAt < u, ct);
        var closed = await (from i in db.Set<Intake>().AsNoTracking()
                            join q in db.Set<Quote>().AsNoTracking() on i.QuoteId equals q.Id
                            where i.OrganizationId == org && i.CompletedAt >= f && i.CompletedAt < u
                            orderby i.CompletedAt
                            select new { i, q }).ToListAsync(ct);
        var completed = closed.Where(x => x.i.Status == IntakeStatus.Completed).ToList();
        return new AnkaufDayFigures(
            received,
            completed.Count,
            closed.Count(x => x.i.Status == IntakeStatus.Rejected),
            completed.Sum(x => x.i.PaidMinor ?? 0),
            Enum.GetValues<PaymentMethod>().Select(m =>
            {
                var paid = completed.Where(x => x.i.PaymentMethod == m).ToList();
                return new AnkaufPaymentFigure(m.ToString(), paid.Count, paid.Sum(x => x.i.PaidMinor ?? 0));
            }).ToList(),
            completed.Select(x => new AnkaufDeviceFigure(x.i.Id, x.i.CompletedAt!.Value, x.q.Brand, x.q.Model, x.q.Storage, x.i.Imei,
                x.i.InspectedCondition?.ToString(), x.i.PaidMinor ?? 0, x.i.PaymentMethod?.ToString() ?? "", x.i.CashMovementId is not null)).ToList());
    }
}
