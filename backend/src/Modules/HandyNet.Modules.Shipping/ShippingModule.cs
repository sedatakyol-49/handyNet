using FluentValidation;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Shipping.Application;
using HandyNet.Modules.Shipping.Domain;
using HandyNet.Modules.Shipping.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Shipping;

public static class ShippingModule
{
    public static IServiceCollection AddShippingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<ShippingOptions>().Bind(configuration.GetSection(ShippingOptions.Section))
            .Validate(o => !environment.IsProduction() || !o.EnabledCarriers.Contains(Carrier.Simulated),
                "The Simulated carrier must not be enabled in production.")
            .ValidateOnStart();
        services.AddSingleton<IModuleModel, ShippingModel>();
        services.AddScoped<ShipmentService>();
        services.AddScoped<IShipmentGate, ShipmentGate>();
        services.AddScoped<IOutboxHandler, OrderDeliveredShipmentHandler>();
        services.AddScoped<ICarrierAdapter, ManualCarrierAdapter>();
        services.AddScoped<ICarrierAdapter, SimulatedCarrierAdapter>();
        services.AddValidatorsFromAssemblyContaining<ShipmentService>(includeInternalTypes: true);
        services.AddSingleton<TrackingSyncWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<TrackingSyncWorker>());
        return services;
    }

    public static IEndpointRouteBuilder MapShippingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shipments").WithTags("Shipping");

        group.MapGet("/carriers", (ShipmentService service) => Results.Ok(service.AvailableCarriers()))
            .RequireModule(PlatformModule.B2BSales);

        // Limits per enabled carrier, so the UI can explain insurance/size rules before the server enforces them.
        group.MapGet("/carrier-profiles", (ShipmentService service, IOptions<ShippingOptions> options) =>
                Results.Ok(service.AvailableCarriers().Select(c =>
                {
                    var p = options.Value.ProfileFor(Enum.Parse<Carrier>(c));
                    return new
                    {
                        carrier = c,
                        p.StandardLiabilityMinor,
                        p.MaxInsuredValueMinor,
                        p.MaxWeightGrams,
                        p.MaxLengthCm,
                        p.AcceptsLithiumInEquipment,
                        p.AcceptsStandaloneLithiumBatteries,
                    };
                })))
            .RequireModule(PlatformModule.B2BSales);

        group.MapGet("/", async (Guid orderId, ShipmentService service, CancellationToken ct) =>
                Results.Ok(await service.ListForOrderAsync(orderId, ct)))
            .RequirePermission(Permissions.OrdersRead);

        group.MapPost("/", async (CreateShipmentRequest request, IValidator<CreateShipmentRequest> validator,
                ShipmentService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.CreateAsync(request, ct))
                .ToHttp(s => Results.Created($"/api/shipments/{s.Id}", s)))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapGet("/{id:guid}/label", async (Guid id, ShipmentService service, CancellationToken ct) =>
                (await service.GetLabelAsync(id, ct)).ToHttp(pdf => Results.File(pdf, "application/pdf", $"label-{id}.pdf")))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapPost("/{id:guid}/handover", async (Guid id, ShipmentService service, CancellationToken ct) =>
                (await service.HandOverAsync(id, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapPost("/{id:guid}/cancel", async (Guid id, ShipmentService service, CancellationToken ct) =>
                (await service.CancelAsync(id, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapPost("/{id:guid}/sync", async (Guid id, ShipmentService service, CancellationToken ct) =>
                (await service.SyncAsync(id, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        return app;
    }
}
