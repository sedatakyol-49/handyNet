using FluentValidation;
using HandyNet.Modules.Ordering.Application;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Ordering;

public static class OrderingModule
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddOrderingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrderingOptions>().Bind(configuration.GetSection(OrderingOptions.Section));
        services.AddSingleton<IModuleModel, Infrastructure.OrderingModel>();
        services.AddScoped<OrderService>();
        services.AddScoped<IOrderFulfilment>(sp => sp.GetRequiredService<OrderService>());
        services.AddScoped<IOrderDailyFigures, OrderDailyFigures>();
        services.AddSingleton<OrderCompletionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<OrderCompletionWorker>());
        services.AddValidatorsFromAssemblyContaining<OrderService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapOrderingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapPost("/", async (PlaceOrderRequest request, [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
                IValidator<PlaceOrderRequest> validator, OrderService service, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > OrderService.MaxIdempotencyKeyLength)
                    return Problems.From(Error.Validation("order.idempotency_key",
                        $"Header {IdempotencyHeader} is required (max {OrderService.MaxIdempotencyKeyLength} characters)."));

                return await validator.ProblemIfInvalidAsync(request, ct)
                       ?? (await service.PlaceAsync(request, idempotencyKey.Trim(), ct)).ToHttp(r => r.Replayed
                           ? Results.Ok(r.Order)
                           : Results.Created($"/api/orders/{r.Order.Id}", r.Order));
            })
            .RequireModule(PlatformModule.B2BPurchasing)
            .RequirePermission(Permissions.OrdersPlace);

        group.MapGet("/", async (string? role, string? status, int? page, int? pageSize, OrderService service, CancellationToken ct) =>
                Results.Ok(await service.ListAsync(role, status, page, pageSize, ct)))
            .RequirePermission(Permissions.OrdersRead);

        group.MapGet("/{id:guid}", async (Guid id, OrderService service, CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrdersRead);

        group.MapPost("/{id:guid}/cancel", async (Guid id, ReasonRequest? request, OrderService service, CancellationToken ct) =>
                (await service.CancelAsync(id, request?.Reason, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrdersPlace);

        group.MapPost("/{id:guid}/confirm-delivery", async (Guid id, OrderService service, CancellationToken ct) =>
                (await service.ConfirmDeliveryAsync(id, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrdersPlace);

        group.MapPost("/{id:guid}/accept", async (Guid id, OrderService service, CancellationToken ct) =>
                (await service.AcceptAsync(id, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrdersPlace);

        group.MapPost("/{id:guid}/confirm", async (Guid id, OrderService service, CancellationToken ct) =>
                (await service.ConfirmAsync(id, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapPost("/{id:guid}/ship", async (Guid id, OrderService service, CancellationToken ct) =>
                (await service.ShipAsync(id, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapPost("/{id:guid}/reject", async (Guid id, ReasonRequest request, OrderService service, CancellationToken ct) =>
                (await service.RejectAsync(id, request.Reason, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        return app;
    }
}
