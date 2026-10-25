using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Quantities are signed sums (sales positive, Storno/returns negative); amounts are gross cents, signed.</summary>
public sealed record ProductSalesDto(Guid ProductId, string Description, int SoldQuantity, int ReturnedQuantity, int NetQuantity, long GrossMinor,
    long DiscountMinor);

public sealed record CashierSalesDto(Guid UserId, string? Name, int SaleCount, int RefundCount, long GrossMinor);

public sealed record PaymentMethodSalesDto(string Method, int ReceiptCount, long AmountMinor);

/// <summary>
/// Sales report for one session or a date range (optionally one register): by product, by cashier, by payment method. Gross
/// figures only: §25a margin VAT per product would reveal purchase prices (ADR-0008); VAT totals are in the Z-report.
/// </summary>
public sealed record SalesReportDto(
    Guid? SessionId,
    Guid? RegisterId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int ReceiptCount,
    long TotalGrossMinor,
    IReadOnlyList<ProductSalesDto> ByProduct,
    IReadOnlyList<CashierSalesDto> ByCashier,
    IReadOnlyList<PaymentMethodSalesDto> ByPaymentMethod);

internal sealed class ReportService(HandyNetDbContext db, ITenantContext tenant, IUserAccounts users, PosActor actor)
{
    public const int MaxRangeDays = 366;

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>
    /// A session report needs <c>pos.close_session</c> or <c>pos.manage</c>; a date-range report (all cashiers' figures)
    /// needs <c>pos.manage</c>.
    /// </summary>
    public async Task<Result<SalesReportDto>> SalesAsync(Guid? sessionId, Guid? registerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var manage = actor.Has(Permissions.PosManage);
        var receipts = db.Set<Receipt>().AsNoTracking().Where(r => r.OrganizationId == OrganizationId);
        if (sessionId is { } session)
        {
            if (!manage && !actor.Has(Permissions.PosCloseSession))
                return Error.Forbidden("permission.denied", $"Missing permission '{Permissions.PosCloseSession}'.");
            if (!await db.Set<CashSession>().AnyAsync(s => s.Id == session && s.OrganizationId == OrganizationId, ct))
                return Error.NotFound("pos.session_not_found", "Cash session not found.");
            receipts = receipts.Where(r => r.SessionId == session);
        }
        else
        {
            if (!manage)
                return Error.Forbidden("permission.denied", $"Missing permission '{Permissions.PosManage}'.");
            if (from is null || to is null)
                return Error.Validation("pos.date_range", "Give a sessionId or a date range (from, to).");
            if (from >= to || to.Value - from.Value > TimeSpan.FromDays(MaxRangeDays))
                return Error.Validation("pos.date_range", $"'from' must be before 'to', at most {MaxRangeDays} days apart.");
            var fromUtc = from.Value.ToUniversalTime();
            var toUtc = to.Value.ToUniversalTime();
            receipts = receipts.Where(r => r.CreatedAt >= fromUtc && r.CreatedAt < toUtc);
        }
        if (registerId is { } register)
            receipts = receipts.Where(r => r.RegisterId == register);

        var totals = await receipts.GroupBy(_ => 1).Select(g => new { Count = g.Count(), Gross = g.Sum(r => r.TotalGrossMinor) }).FirstOrDefaultAsync(ct);

        var byProduct = await (from l in db.Set<ReceiptLine>()
                               join r in receipts on l.ReceiptId equals r.Id
                               where l.ProductId != null
                               group l by l.ProductId into g
                               select new
                               {
                                   ProductId = g.Key!.Value,
                                   Description = g.Max(x => x.Description),
                                   Sold = g.Sum(x => x.Quantity > 0 ? x.Quantity : 0),
                                   Returned = g.Sum(x => x.Quantity < 0 ? -x.Quantity : 0),
                                   Net = g.Sum(x => x.Quantity),
                                   Gross = g.Sum(x => x.LineGrossMinor),
                                   Discount = g.Sum(x => x.DiscountMinor),
                               }).ToListAsync(ct);

        var byCashier = await receipts.GroupBy(r => r.CreatedBy)
            .Select(g => new
            {
                UserId = g.Key,
                Sales = g.Count(r => r.Kind == ReceiptKind.Sale),
                Refunds = g.Count(r => r.Kind == ReceiptKind.Storno || r.Kind == ReceiptKind.Return),
                Gross = g.Sum(r => r.TotalGrossMinor),
            }).ToListAsync(ct);

        var byMethod = await (from p in db.Set<ReceiptPayment>()
                              join r in receipts on p.ReceiptId equals r.Id
                              group p by p.Method into g
                              select new { Method = g.Key, Count = g.Count(), Amount = g.Sum(x => x.AmountMinor) }).ToListAsync(ct);

        var people = await users.GetAsync(byCashier.Select(c => c.UserId).ToList(), ct);
        return new SalesReportDto(sessionId, registerId, sessionId is null ? from : null, sessionId is null ? to : null, totals?.Count ?? 0, totals?.Gross ?? 0,
            byProduct.OrderByDescending(p => p.Gross).ThenBy(p => p.Description)
                .Select(p => new ProductSalesDto(p.ProductId, p.Description, p.Sold, p.Returned, p.Net, p.Gross, p.Discount)).ToList(),
            byCashier.OrderByDescending(c => c.Gross)
                .Select(c => new CashierSalesDto(c.UserId, people.GetValueOrDefault(c.UserId)?.DisplayName, c.Sales, c.Refunds, c.Gross)).ToList(),
            Enum.GetValues<PaymentMethod>().Select(m => byMethod.FirstOrDefault(x => x.Method == m) is { } x
                ? new PaymentMethodSalesDto(m.ToString(), x.Count, x.Amount)
                : new PaymentMethodSalesDto(m.ToString(), 0, 0)).ToList());
    }
}
