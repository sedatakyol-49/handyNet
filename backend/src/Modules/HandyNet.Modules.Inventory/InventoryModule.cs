using FluentValidation;
using HandyNet.Modules.Inventory.Application;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HandyNet.Modules.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.InventoryModel>();
        services.AddScoped<InventoryService>();
        services.AddScoped<IInventory>(sp => sp.GetRequiredService<InventoryService>());
        services.AddScoped<UnitService>();
        services.AddScoped<ImeiBlacklistService>();
        // No external lost/stolen database is connected; a real adapter replaces this registration (ADR-0019).
        services.TryAddSingleton<IExternalImeiCheck, DisabledExternalImeiCheck>();
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

        // productId: the stock of one product in every warehouse (Lager product panel).
        group.MapGet("/stock", async (Guid? warehouseId, Guid? productId, int? page, int? pageSize, InventoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListStockAsync(warehouseId, page, pageSize, ct, productId)));

        group.MapPost("/adjustments", async (AdjustStockRequest request, IValidator<AdjustStockRequest> validator,
                InventoryService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.AdjustAsync(request, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.InventoryManage);

        group.MapGet("/movements", async (Guid? productId, int? page, int? pageSize, InventoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListMovementsAsync(productId, page, pageSize, ct)));

        // ---- serialized units (Geräte nach IMEI): list and history; devices enter stock via Ankauf or the Lager entry -
        group.MapGet("/units", async (Guid? productId, UnitStatus? status, string? imei, Guid? warehouseId, int? page, int? pageSize,
                UnitService service, CancellationToken ct) =>
            (await service.ListAsync(productId, status, imei, warehouseId, page, pageSize, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.InventoryManage);

        group.MapGet("/units/{id:guid}", async (Guid id, UnitService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.InventoryManage);

        // ---- platform IMEI blacklist (platform administrators only, ADR-0019) -----------------------------------------
        var admin = app.MapGroup("/api/admin/imei-blacklist").WithTags("Inventory administration").RequirePlatformAdmin();

        admin.MapGet("/", async (string? imei, bool? active, int? page, int? pageSize, ImeiBlacklistService service, CancellationToken ct) =>
            (await service.ListAsync(imei, active, page, pageSize, ct)).ToHttp(Results.Ok));

        admin.MapPost("/", async (AddBlacklistEntryRequest request, IValidator<AddBlacklistEntryRequest> validator,
                ImeiBlacklistService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.AddAsync(request, ct)).ToHttp(e => Results.Created($"/api/admin/imei-blacklist/{e.Id}", e)));

        admin.MapPost("/{id:guid}/deactivate", async (Guid id, DeactivateBlacklistEntryRequest request,
                IValidator<DeactivateBlacklistEntryRequest> validator, ImeiBlacklistService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.DeactivateAsync(id, request, ct)).ToHttp(Results.Ok));

        return app;
    }
}
