using HandyNet.Modules.Shipping.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Shipping.Application;

/// <summary>
/// Polls carriers for active shipments. Due shipments come from the SECURITY DEFINER function
/// <c>shipping.shipments_due_for_tracking</c> (ids only); each is synced in its own scope as the seller.
/// </summary>
public sealed class TrackingSyncWorker(IServiceScopeFactory scopeFactory, IOptions<ShippingOptions> options, ILogger<TrackingSyncWorker> logger)
    : BackgroundService
{
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
                logger.LogError(ex, "Tracking sync run failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.TrackingSyncMinutes)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct, int minAgeMinutes = -1)
    {
        var minAge = minAgeMinutes >= 0 ? minAgeMinutes : options.Value.TrackingSyncMinutes;
        List<DueShipment> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
            due = await db.Database
                .SqlQuery<DueShipment>($"SELECT id, seller_organization_id FROM shipping.shipments_due_for_tracking({minAge}, 200)")
                .ToListAsync(ct);
        }

        var synced = 0;
        foreach (var shipment in due)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(shipment.SellerOrganizationId);
            var result = await scope.ServiceProvider.GetRequiredService<ShipmentService>().SyncAsync(shipment.Id, ct);
            if (result.IsSuccess)
                synced++;
            else
                logger.LogWarning("Tracking sync for {ShipmentId} failed: {Code}", shipment.Id, result.Error!.Code);
        }

        return synced;
    }

    private sealed record DueShipment(Guid Id, Guid SellerOrganizationId);
}
