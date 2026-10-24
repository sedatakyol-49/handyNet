using HandyNet.Modules.Payments.Contracts;
using HandyNet.Modules.Payments.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Payments.Application;

internal sealed class PaymentDailyFigures(HandyNetDbContext db, ITenantContext tenant) : IPaymentDailyFigures
{
    public async Task<PaymentDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var org = tenant.RequiredOrganizationId;
        var f = from.ToUniversalTime();
        var u = until.ToUniversalTime();
        var payments = await db.Set<Payment>().AsNoTracking()
            .Where(p => (p.BuyerOrganizationId == org || p.SellerOrganizationId == org)
                        && ((p.PaidAt >= f && p.PaidAt < u) || (p.RefundedAt >= f && p.RefundedAt < u)))
            .Select(p => new { p.BuyerOrganizationId, p.SellerOrganizationId, p.PaidAt, p.RefundedAt, p.AmountMinor })
            .ToListAsync(ct);
        bool In(DateTimeOffset? at) => at is { } t && t >= f && t < u;
        var paidBuyer = payments.Where(p => p.BuyerOrganizationId == org && In(p.PaidAt)).ToList();
        var paidSeller = payments.Where(p => p.SellerOrganizationId == org && In(p.PaidAt)).ToList();
        var refundBuyer = payments.Where(p => p.BuyerOrganizationId == org && In(p.RefundedAt)).ToList();
        var refundSeller = payments.Where(p => p.SellerOrganizationId == org && In(p.RefundedAt)).ToList();
        return new PaymentDayFigures(paidBuyer.Count, paidBuyer.Sum(p => p.AmountMinor), paidSeller.Count, paidSeller.Sum(p => p.AmountMinor),
            refundBuyer.Count, refundBuyer.Sum(p => p.AmountMinor), refundSeller.Count, refundSeller.Sum(p => p.AmountMinor));
    }
}
