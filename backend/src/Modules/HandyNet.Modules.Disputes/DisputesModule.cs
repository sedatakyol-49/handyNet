using FluentValidation;
using HandyNet.Modules.Disputes.Application;
using HandyNet.Modules.Disputes.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Disputes;

public static class DisputesModule
{
    public sealed record DecisionRequest(bool Refund, string Resolution);

    public static IServiceCollection AddDisputesModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, DisputesModel>();
        services.AddScoped<DisputeService>();
        services.AddScoped<DisputeAdministration>();
        services.AddValidatorsFromAssemblyContaining<DisputeService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapDisputesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orders/{orderId:guid}/disputes", async (Guid orderId, OpenDisputeRequest request,
                IValidator<OpenDisputeRequest> validator, DisputeService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.OpenAsync(orderId, request, ct))
                .ToHttp(d => Results.Created($"/api/disputes/{d.Id}", d)))
            .WithTags("Disputes")
            .RequirePermission(Permissions.OrdersPlace);

        var group = app.MapGroup("/api/disputes").WithTags("Disputes").RequirePermission(Permissions.OrdersRead);

        group.MapGet("/", async (Guid? orderId, DisputeService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(orderId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, DisputeService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        group.MapPost("/{id:guid}/messages", async (Guid id, TextRequest request, DisputeService service, CancellationToken ct) =>
            (await service.AddMessageAsync(id, request.Text, ct)).ToHttp(Results.Ok));

        group.MapPost("/{id:guid}/escalate", async (Guid id, TextRequest? request, DisputeService service, CancellationToken ct) =>
            (await service.EscalateAsync(id, request?.Text ?? string.Empty, ct)).ToHttp(Results.Ok));

        group.MapPost("/{id:guid}/refund", async (Guid id, TextRequest request, DisputeService service, CancellationToken ct) =>
                (await service.GrantRefundAsync(id, request.Text, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrdersFulfil);

        group.MapPost("/{id:guid}/withdraw", async (Guid id, TextRequest request, DisputeService service, CancellationToken ct) =>
                (await service.WithdrawAsync(id, request.Text, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrdersPlace);

        var admin = app.MapGroup("/api/admin/disputes").WithTags("Administration").RequirePlatformAdmin();

        admin.MapGet("/", async (DisputeAdministration administration, CancellationToken ct) =>
            Results.Ok(await administration.ListEscalatedAsync(ct)));

        admin.MapGet("/{id:guid}", async (Guid id, DisputeAdministration administration, CancellationToken ct) =>
            (await administration.GetAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/decide", async (Guid id, DecisionRequest request, DisputeAdministration administration,
                ICurrentUser user, CancellationToken ct) =>
            (await administration.DecideAsync(id, request.Refund, request.Resolution, user.UserId, ct)).ToHttp(Results.Ok));

        return app;
    }
}
