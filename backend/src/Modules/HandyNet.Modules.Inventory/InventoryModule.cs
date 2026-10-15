using FluentValidation;
using HandyNet.Modules.Inventory.Application;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.InventoryModel>();
        services.AddScoped<InventoryService>();
        services.AddScoped<IInventory>(sp => sp.GetRequiredService<InventoryService>());
        services.AddValidatorsFromAssemblyContaining<InventoryService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inventory").WithTags("Inventory").RequireModule(PlatformModule.Inventory);

        group.MapGet("/warehouses", async (InventoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListWarehousesAsync(ct)));

        group.MapPost("/warehouses", async (CreateWarehouseRequest request, IValidator<CreateWarehouseRequest> validator,
                InventoryService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.CreateWarehouseAsync(request, ct))
                .ToHttp(w => Results.Created($"/api/inventory/warehouses/{w.Id}", w)))
            .RequirePermission(Permissions.InventoryManage);

        group.MapGet("/stock", async (Guid? warehouseId, int? page, int? pageSize, InventoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListStockAsync(warehouseId, page, pageSize, ct)));

        group.MapPost("/adjustments", async (AdjustStockRequest request, IValidator<AdjustStockRequest> validator,
                InventoryService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.AdjustAsync(request, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.InventoryManage);

        group.MapGet("/movements", async (Guid? productId, int? page, int? pageSize, InventoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListMovementsAsync(productId, page, pageSize, ct)));

        return app;
    }
}
