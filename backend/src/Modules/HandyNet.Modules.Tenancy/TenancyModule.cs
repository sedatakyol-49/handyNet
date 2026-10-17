using FluentValidation;
using HandyNet.Modules.Identity;
using HandyNet.Modules.Tenancy.Application;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.TenancyModel>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<AdministrationService>();
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();
        services.AddValidatorsFromAssemblyContaining<OrganizationService>(includeInternalTypes: true);
        return services;
    }

    /// <summary>Must be placed after authentication and before any endpoint that touches tenant data.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();

    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder app)
    {
        var registration = app.MapGroup("/api/registration").WithTags("Registration");

        registration.MapPost("/", async (RegisterBusinessRequest request, IValidator<RegisterBusinessRequest> validator,
                OrganizationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.RegisterBusinessAsync(request, ct))
                .ToHttp(r => Results.Created($"/api/organizations/{r.OrganizationId}", r)))
            .AllowAnonymous()
            .RequireRateLimiting(IdentityModule.AuthRateLimitPolicy);

        app.MapGet("/api/me", async (OrganizationService service, CancellationToken ct) =>
                Results.Ok(await service.GetMeAsync(ct)))
            .WithTags("Organizations")
            .RequireAuthorization();

        var organizations = app.MapGroup("/api/organizations").WithTags("Organizations").RequireAuthorization();

        organizations.MapPost("/", async (OrganizationInput input, IValidator<OrganizationInput> validator,
                OrganizationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(input, ct) ?? (await service.RegisterAdditionalAsync(input, ct))
                .ToHttp(id => Results.Created($"/api/organizations/{id}", new { id })));

        var current = app.MapGroup("/api/organization").WithTags("Organizations");

        current.MapGet("/staff", async (OrganizationService service, CancellationToken ct) =>
                Results.Ok(await service.ListStaffAsync(ct)))
            .RequirePermission(Permissions.OrganizationManage);

        current.MapPost("/staff", async (AddStaffRequest request, IValidator<AddStaffRequest> validator,
                OrganizationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.AddStaffAsync(request, ct))
                .ToHttp(id => Results.Ok(new { userId = id })))
            .RequirePermission(Permissions.OrganizationManage);

        // Per-cashier POS rights (price override, discount limit, refunds, cash in/out, closing) of a staff member.
        current.MapPut("/staff/{userId:guid}/pos-rights", async (Guid userId, SetPosRightsRequest request, IValidator<SetPosRightsRequest> validator,
                OrganizationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SetPosRightsAsync(userId, request, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrganizationManage);

        var admin = app.MapGroup("/api/admin/organizations").WithTags("Administration").RequirePlatformAdmin();

        admin.MapGet("/", async (string? status, string? search, int? page, int? pageSize,
                AdministrationService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(status, search, page, pageSize, ct)));

        admin.MapPost("/{id:guid}/verify", async (Guid id, AdministrationService service, CancellationToken ct) =>
            (await service.VerifyAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/reject", async (Guid id, ReasonRequest request, AdministrationService service, CancellationToken ct) =>
            (await service.RejectAsync(id, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/suspend", async (Guid id, ReasonRequest request, AdministrationService service, CancellationToken ct) =>
            (await service.SuspendAsync(id, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPut("/{id:guid}/modules", async (Guid id, SetModulesRequest request, AdministrationService service, CancellationToken ct) =>
            (await service.SetModulesAsync(id, request.Modules, ct)).ToHttp(Results.Ok));

        app.MapGet("/api/admin/modules", () => Results.Ok(Enum.GetNames<PlatformModule>()))
            .WithTags("Administration")
            .RequirePlatformAdmin();

        return app;
    }
}
