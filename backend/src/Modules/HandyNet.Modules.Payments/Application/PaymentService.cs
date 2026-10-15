using HandyNet.Modules.Ledger.Contracts;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Payments.Domain;
using HandyNet.Modules.Payments.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Payments.Application;

public sealed record PaymentDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string Provider,
    string Status,
    long AmountMinor,
    string Currency,
    string? CheckoutUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? RefundedAt,
    long? CommissionNetMinor,
    long? CommissionVatMinor,
    long? SellerShareMinor);

public sealed record PayoutDto(Guid Id, Guid OrderId, string OrderNumber, long AmountMinor, string Currency, string ProviderTransferId, DateTimeOffset CreatedAt);

public sealed record SellerBalanceDto(long OpenBalanceMinor, string Currency, IReadOnlyList<PayoutDto> RecentPayouts);

internal sealed class PaymentService(
    HandyNetDbContext db,
    ITenantContext tenant,
    IOrderFulfilment orders,
    IEnumerable<IPaymentProvider> providers,
    ILedger ledger,
    IOptions<PaymentOptions> options,
    IClock clock)
{
    private static readonly string[] PayableOrderStates = [nameof(OrderStatus.Placed), nameof(OrderStatus.Confirmed)];

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>Buyer starts (or resumes) online payment for an order. Returns the open payment if one exists.</summary>
    public async Task<Result<PaymentDto>> StartAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.GetForFulfilmentAsync(orderId, ct);
        if (order is null || order.BuyerOrganizationId != OrganizationId)
            return Error.NotFound("order.not_found", "Order not found.");

        var live = await db.Set<Payment>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrderId == orderId && (p.Status == PaymentStatus.Open || p.Status == PaymentStatus.Paid), ct);
        if (live?.Status == PaymentStatus.Paid)
            return Error.Conflict("payment.already_paid", "This order has already been paid.");
        if (live is not null)
            return ToDto(live);
        if (!PayableOrderStates.Contains(order.Status))
            return Error.Conflict("payment.order_not_payable", $"Orders in status {order.Status} cannot be paid.");

        var provider = options.Value.EnabledProviders.Select(e => providers.FirstOrDefault(p => p.Provider == e)).FirstOrDefault(p => p is not null);
        if (provider is null)
            return Error.Validation("payment.not_available", "Online payment is not available.");

        var o = options.Value;
        var split = CommissionSplit.Calculate(order.GrossTotalMinor, order.NetTotalMinor, o.CommissionBasisPoints, o.CommissionVatBasisPoints);
        var paymentId = Guid.CreateVersion7();
        var created = await provider.CreatePaymentAsync(new ProviderPaymentRequest(
            paymentId, order.Id, order.Number, order.GrossTotalMinor, Money.Eur, order.SellerOrganizationId,
            split.CommissionNetMinor + split.CommissionVatMinor,
            ReturnUrl: $"{o.WebBaseUrl.TrimEnd('/')}/app/bestellungen/{order.Id}",
            WebhookUrl: $"{o.ApiBaseUrl.TrimEnd('/')}/api/payments/webhooks/{provider.Provider.ToString().ToLowerInvariant()}"), ct);
        if (!created.IsSuccess)
            return created.Error!;

        var payment = Payment.Create(paymentId, order.Id, order.Number, order.BuyerOrganizationId, order.SellerOrganizationId, provider.Provider,
            created.Value.ProviderPaymentId, created.Value.CheckoutUrl, split, o.CommissionBasisPoints, clock.UtcNow);
        if (!payment.IsSuccess)
            return payment.Error!;

        db.Add(payment.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Concurrent "pay" clicks: the other request's payment is the live one; ours is abandoned at the provider.
            await provider.CancelAsync(created.Value.ProviderPaymentId, ct);
            db.ChangeTracker.Clear();
            var winner = await db.Set<Payment>().AsNoTracking()
                .FirstAsync(p => p.OrderId == orderId && (p.Status == PaymentStatus.Open || p.Status == PaymentStatus.Paid), ct);
            return ToDto(winner);
        }

        return ToDto(payment.Value);
    }

    public async Task<IReadOnlyList<PaymentDto>> ListForOrderAsync(Guid orderId, CancellationToken ct) =>
        (await db.Set<Payment>().AsNoTracking()
            .Where(p => p.OrderId == orderId && (p.BuyerOrganizationId == OrganizationId || p.SellerOrganizationId == OrganizationId))
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<SellerBalanceDto> GetSellerBalanceAsync(CancellationToken ct)
    {
        var balances = await ledger.GetOwnBalancesAsync(ct);
        var open = balances.Where(b => b.Account == Ledger.Domain.AccountType.SellerPayable && b.Currency == Money.Eur).Sum(b => b.BalanceMinor);
        var payouts = await (
                from payout in db.Set<Payout>().AsNoTracking()
                join payment in db.Set<Payment>().AsNoTracking() on payout.PaymentId equals payment.Id
                where payout.SellerOrganizationId == OrganizationId
                orderby payout.CreatedAt descending
                select new PayoutDto(payout.Id, payout.OrderId, payment.OrderNumber, payout.AmountMinor, payout.Currency,
                    payout.ProviderTransferId, payout.CreatedAt))
            .Take(50)
            .ToListAsync(ct);
        return new SellerBalanceDto(open, Money.Eur, payouts);
    }

    private PaymentDto ToDto(Payment p)
    {
        var isSeller = p.SellerOrganizationId == tenant.OrganizationId;
        return new PaymentDto(p.Id, p.OrderId, p.OrderNumber, p.Provider.ToString(), p.Status.ToString(), p.AmountMinor, p.Currency,
            p.Status == PaymentStatus.Open && !isSeller ? p.CheckoutUrl : null, p.CreatedAt, p.PaidAt, p.RefundedAt,
            isSeller ? p.CommissionNetMinor : null, isSeller ? p.CommissionVatMinor : null, isSeller ? p.Split.SellerShareMinor : null);
    }
}
