using System.Text.Json;
using HandyNet.Modules.Ledger.Contracts;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Payments.Domain;
using HandyNet.Modules.Payments.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Payments.Application;

/// <summary>
/// State changes of payments that are driven by the provider or by the order lifecycle. Every operation is
/// idempotent (terminal states, unique payout per payment, ledger idempotency keys) because webhooks and outbox
/// events are delivered at least once. Runs inside a scope whose tenant is the payment's seller.
/// </summary>
internal sealed class PaymentProcessor(
    HandyNetDbContext db,
    ITenantContext tenant,
    IEnumerable<IPaymentProvider> providers,
    ILedger ledger,
    IOrderFulfilment orders,
    IClock clock,
    ILogger<PaymentProcessor> logger)
{
    private static readonly string[] ClosedOrderStates = [nameof(OrderStatus.Cancelled), nameof(OrderStatus.Rejected), nameof(OrderStatus.Refunded)];

    /// <summary>Fetches the authoritative status from the provider and applies it (webhook / return page).</summary>
    public async Task<Result> SyncAsync(Guid paymentId, CancellationToken ct) =>
        await InTransactionAsync(paymentId, async payment =>
        {
            var reported = await Provider(payment).GetStatusAsync(payment.ProviderPaymentId, ct);
            var change = payment.Apply(reported, clock.UtcNow);
            if (change != PaymentChange.BecamePaid)
                return Result.Success();

            var captured = await ledger.PostAsync(PaymentJournal.Capture(payment, payment.PaidAt!.Value), ct);
            if (!captured.IsSuccess)
                return captured;

            // Paid after the order was already cancelled/rejected (race between buyer and seller): give the money back.
            var order = await orders.GetForFulfilmentAsync(payment.OrderId, ct);
            if (order is not null && ClosedOrderStates.Contains(order.Status))
                return await RefundPaidAsync(payment, ct);
            return Result.Success();
        }, ct);

    /// <summary>Order cancelled or rejected: refund a paid payment, cancel an open one.</summary>
    public async Task<Result> RefundOrCancelAsync(Guid paymentId, CancellationToken ct) =>
        await InTransactionAsync(paymentId, async payment => payment.Status switch
        {
            PaymentStatus.Paid => await RefundPaidAsync(payment, ct),
            PaymentStatus.Open => await CancelOpenAsync(payment, ct),
            _ => Result.Success(),
        }, ct);

    /// <summary>Order completed (inspection window over or accepted): release the seller's share.</summary>
    public async Task<Result> PayOutAsync(Guid paymentId, CancellationToken ct) =>
        await InTransactionAsync(paymentId, async payment =>
        {
            if (payment.Status != PaymentStatus.Paid || payment.Split.SellerShareMinor == 0)
                return Result.Success();
            if (await db.Set<Payout>().AnyAsync(p => p.PaymentId == payment.Id, ct))
                return Result.Success();
            var order = await orders.GetForFulfilmentAsync(payment.OrderId, ct);
            if (order?.Status != nameof(OrderStatus.Completed))
                return Error.Conflict("payout.order_not_completed", "Payouts happen only for completed orders.");

            var released = await Provider(payment).ReleaseToSellerAsync(payment.ProviderPaymentId, payment.SellerOrganizationId,
                payment.Split.SellerShareMinor, ct);
            if (!released.IsSuccess)
                return released.Error!;

            var payout = Payout.Create(payment, released.Value, clock.UtcNow);
            db.Add(payout);
            var posted = await ledger.PostAsync(PaymentJournal.Payout(payment, payout.Id, clock.UtcNow), ct);
            return posted.IsSuccess ? Result.Success() : posted.Error!;
        }, ct);

    private async Task<Result> RefundPaidAsync(Payment payment, CancellationToken ct)
    {
        var refunded = await Provider(payment).RefundAsync(payment.ProviderPaymentId, payment.AmountMinor, ct);
        if (!refunded.IsSuccess)
            return refunded;
        var marked = payment.MarkRefunded(clock.UtcNow);
        if (!marked.IsSuccess)
            return marked;
        var posted = await ledger.PostAsync(PaymentJournal.Refund(payment, clock.UtcNow), ct);
        return posted.IsSuccess ? Result.Success() : posted.Error!;
    }

    private async Task<Result> CancelOpenAsync(Payment payment, CancellationToken ct)
    {
        var cancelled = await Provider(payment).CancelAsync(payment.ProviderPaymentId, ct);
        return cancelled.IsSuccess ? payment.CancelOpen() : cancelled;
    }

    private IPaymentProvider Provider(Payment payment) =>
        providers.FirstOrDefault(p => p.Provider == payment.Provider)
        ?? throw new InvalidOperationException($"Payment provider {payment.Provider} is not registered.");

    private async Task<Result> InTransactionAsync(Guid paymentId, Func<Payment, Task<Result>> action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = await db.Set<Payment>().FirstOrDefaultAsync(p => p.Id == paymentId && p.SellerOrganizationId == tenant.OrganizationId, ct);
        if (payment is null)
            return Error.NotFound("payment.not_found", "Payment not found.");

        var result = await action(payment);
        if (!result.IsSuccess)
        {
            logger.LogWarning("Payment {PaymentId}: {Code} {Message}", paymentId, result.Error!.Code, result.Error.Message);
            return result;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            // A concurrent delivery of the same webhook/event got there first; the other one wins.
            logger.LogInformation("Concurrent processing of payment {PaymentId} detected; skipping", paymentId);
            return Result.Success();
        }
    }
}

/// <summary>
/// Entry points without a tenant (provider webhooks, outbox events). A SECURITY DEFINER function reveals only the
/// payment id and its seller; processing then runs in a fresh scope as the seller's system actor (ADR-0005).
/// </summary>
public sealed class PaymentRunner(IServiceScopeFactory scopeFactory)
{
    public async Task<bool> SyncByProviderIdAsync(PaymentProvider provider, string providerPaymentId, CancellationToken ct)
    {
        var owners = await FindAsync($"SELECT id, seller_organization_id FROM payments.payment_owner({provider.ToString()}, {providerPaymentId})", ct);
        foreach (var owner in owners)
            await RunAsync(owner, (p, id) => p.SyncAsync(id, ct));
        return owners.Count > 0;
    }

    public async Task ForOrderAsync(Guid orderId, bool completed, CancellationToken ct)
    {
        var owners = await FindAsync($"SELECT id, seller_organization_id FROM payments.payments_for_order({orderId})", ct);
        foreach (var owner in owners)
        {
            var result = await RunAsync(owner, (p, id) => completed ? p.PayOutAsync(id, ct) : p.RefundOrCancelAsync(id, ct));
            if (!result.IsSuccess)
                throw new InvalidOperationException($"Payment {owner.Id}: {result.Error!.Code}"); // outbox retries
        }
    }

    private async Task<List<Owner>> FindAsync(FormattableString sql, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
        return await db.Database.SqlQuery<Owner>(sql).ToListAsync(ct);
    }

    private async Task<Result> RunAsync(Owner owner, Func<PaymentProcessor, Guid, Task<Result>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(owner.SellerOrganizationId);
        return await action(scope.ServiceProvider.GetRequiredService<PaymentProcessor>(), owner.Id);
    }

    private sealed record Owner(Guid Id, Guid SellerOrganizationId);
}

/// <summary>Reacts to order lifecycle events: completed → payout; cancelled/rejected → refund or cancel.</summary>
internal sealed class OrderStatusPaymentHandler(PaymentRunner runner) : IOutboxHandler
{
    public string EventType => typeof(OrderStatusChanged).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<OrderStatusChanged>(payload)!;
        if (e.To == nameof(OrderStatus.Completed))
            await runner.ForOrderAsync(e.OrderId, completed: true, ct);
        else if (e.To is nameof(OrderStatus.Cancelled) or nameof(OrderStatus.Rejected) or nameof(OrderStatus.Refunded))
            await runner.ForOrderAsync(e.OrderId, completed: false, ct);
    }
}
