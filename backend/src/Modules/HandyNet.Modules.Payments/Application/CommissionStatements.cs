using HandyNet.Modules.Payments.Contracts;
using HandyNet.Modules.Payments.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Payments.Application;

/// <summary>Runs under RLS: payments are visible to their buyer and seller; we additionally filter on the seller.</summary>
internal sealed class CommissionStatements(HandyNetDbContext db, ITenantContext tenant) : ICommissionStatements
{
    public async Task<IReadOnlyList<Guid>> GetSellersWithActivityAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct) =>
        await db.Database
            .SqlQuery<Guid>($"SELECT seller_organization_id AS \"Value\" FROM payments.commission_sellers({from}, {until})")
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CommissionCharge>> GetCapturedAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var seller = tenant.RequiredOrganizationId;
        return await db.Set<Payment>().AsNoTracking()
            .Where(p => p.SellerOrganizationId == seller && p.Status == PaymentStatus.Paid && p.PaidAt >= from && p.PaidAt < until)
            .OrderBy(p => p.PaidAt).ThenBy(p => p.Id)
            .Select(p => new CommissionCharge(p.Id, p.OrderId, p.OrderNumber, p.PaidAt!.Value, p.RefundedAt, p.CommissionNetMinor,
                p.CommissionVatMinor, p.Currency))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CommissionCharge>> GetRefundedAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var seller = tenant.RequiredOrganizationId;
        return await db.Set<Payment>().AsNoTracking()
            .Where(p => p.SellerOrganizationId == seller && p.Status == PaymentStatus.Refunded && p.PaidAt != null
                        && p.RefundedAt >= from && p.RefundedAt < until)
            .OrderBy(p => p.RefundedAt).ThenBy(p => p.Id)
            .Select(p => new CommissionCharge(p.Id, p.OrderId, p.OrderNumber, p.PaidAt!.Value, p.RefundedAt, p.CommissionNetMinor,
                p.CommissionVatMinor, p.Currency))
            .ToListAsync(ct);
    }
}
