using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Ordering.Application;

/// <summary>
/// Completes delivered orders whose inspection window has passed without a complaint. Due orders are
/// found through the SECURITY DEFINER function <c>ordering.orders_due_for_completion</c> (it exposes only
/// ids); each order is then processed in its own scope acting as the seller, so RLS still applies.
/// </summary>
public sealed class OrderCompletionWorker(IServiceScopeFactory scopeFactory, ILogger<OrderCompletionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Order completion run failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<DueOrder> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
            due = await db.Database
                .SqlQuery<DueOrder>($"SELECT id, seller_organization_id FROM ordering.orders_due_for_completion(100)")
                .ToListAsync(ct);
        }

        var completed = 0;
        foreach (var order in due)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(order.SellerOrganizationId);
            var result = await scope.ServiceProvider.GetRequiredService<IOrderFulfilment>().CompleteAfterInspectionAsync(order.Id, ct);
            if (result.IsSuccess)
                completed++;
            else
                logger.LogWarning("Could not complete order {OrderId}: {Code}", order.Id, result.Error!.Code);
        }

        return completed;
    }

    private sealed record DueOrder(Guid Id, Guid SellerOrganizationId);
}
