using FluentValidation;
using HandyNet.Modules.Identity;
using HandyNet.Modules.Identity.Contracts;
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
        services.AddScoped<IOrganizationModuleDirectory, OrganizationModuleDirectory>();
        services.AddScoped<IUserMembershipSource, UserMembershipSource>();
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

        // Companies (Unternehmen): list with filters and counts, detail (access-logged), master data, status, modules,
        // memberships. Platform data only; tenants' operational data is out of scope (ADR-0013).
        admin.MapGet("/", async ([AsParameters] AdminOrganizationQuery query, IValidator<AdminOrganizationQuery> validator,
                AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(query, ct) ?? (await service.ListAsync(query, ct)).ToHttp(Results.Ok));

        admin.MapGet("/{id:guid}", async (Guid id, AdministrationService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapPut("/{id:guid}", async (Guid id, AdminUpdateOrganizationRequest request, IValidator<AdminUpdateOrganizationRequest> validator,
                AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/verify", async (Guid id, AdministrationService service, CancellationToken ct) =>
            (await service.VerifyAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/reject", async (Guid id, ReasonRequest request, IValidator<ReasonRequest> validator,
                AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.RejectAsync(id, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/suspend", async (Guid id, ReasonRequest request, IValidator<ReasonRequest> validator,
                AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SuspendAsync(id, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/reactivate", async (Guid id, ReasonRequest request, IValidator<ReasonRequest> validator,
                AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ReactivateAsync(id, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPut("/{id:guid}/modules", async (Guid id, SetModulesRequest request, AdministrationService service, CancellationToken ct) =>
            (await service.SetModulesAsync(id, request.Modules, ct)).ToHttp(Results.Ok));

        admin.MapPut("/{id:guid}/members/{userId:guid}/role", async (Guid id, Guid userId, ChangeMemberRoleRequest request,
                IValidator<ChangeMemberRoleRequest> validator, AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ChangeMemberRoleAsync(id, userId, request, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/members/{userId:guid}/remove", async (Guid id, Guid userId, ReasonRequest request,
                IValidator<ReasonRequest> validator, AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.RemoveMemberAsync(id, userId, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/{id:guid}/transfer-ownership", async (Guid id, TransferOwnershipRequest request, IValidator<TransferOwnershipRequest> validator,
                AdministrationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.TransferOwnershipAsync(id, request, ct)).ToHttp(Results.Ok));

        app.MapGet("/api/admin/modules", () => Results.Ok(Enum.GetNames<PlatformModule>()))
            .WithTags("Administration")
            .RequirePlatformAdmin();

        return app;
    }
}
