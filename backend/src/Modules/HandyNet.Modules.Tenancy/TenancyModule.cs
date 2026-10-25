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
using Microsoft.Net.Http.Headers;

namespace HandyNet.Modules.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.TenancyModel>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<AdministrationService>();
        services.AddScoped<CompanyProfileService>();
        services.AddScoped<IOrganizationBranding, OrganizationBrandingSource>();
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

        // Own company profile (ADR-0014): every member reads, organization.manage (owners) writes. Always the tenant of the
        // request — no organization id in the route.
        current.MapGet("/profile", async (CompanyProfileService service, CancellationToken ct) =>
                (await service.GetAsync(ct)).ToHttp(Results.Ok))
            .RequireTenant();

        current.MapPut("/profile", async (UpdateCompanyProfileRequest request, IValidator<UpdateCompanyProfileRequest> validator,
                CompanyProfileService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(request, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrganizationManage);

        // Multipart form field "file": PNG/JPEG/WebP up to 2 MB, checked by magic bytes, re-encoded (metadata stripped).
        current.MapPost("/logo", async (IFormFile? file, CompanyProfileService service, CancellationToken ct) =>
            {
                if (file is null)
                    return Problems.From(SharedKernel.Error.Validation("image.file_missing", "Send the image as multipart form field 'file'."));
                if (Domain.OrganizationLogo.Limits.CheckSize(file.Length) is { } sizeError)
                    return Problems.From(sizeError);
                using var buffer = new MemoryStream((int)file.Length);
                await using (var stream = file.OpenReadStream())
                    await stream.CopyToAsync(buffer, ct);
                return (await service.UploadLogoAsync(buffer.ToArray(), ct)).ToHttp(l => Results.Created(l.ScreenUrl, l));
            })
            .RequirePermission(Permissions.OrganizationManage)
            .DisableAntiforgery() // JWT bearer API, no cookies
            .WithMetadata(new RequestBodyLimit(Domain.OrganizationLogo.MaxUploadBytes + 64 * 1024));

        current.MapDelete("/logo", async (CompanyProfileService service, CancellationToken ct) =>
                (await service.RemoveLogoAsync(ct)).ToHttp(Results.NoContent))
            .RequirePermission(Permissions.OrganizationManage);

        // Public on purpose: the logo is printed on receipts and shown on public e-receipt pages. Random id (UUIDv4) as
        // token, content behind an id never changes → immutable caching; removed logos are 404.
        app.MapGet("/api/logos/{logoId:guid}/{variant}", async (Guid logoId, string variant, HttpContext http,
                CompanyProfileService service, CancellationToken ct) =>
            {
                LogoVariant? parsed = variant switch { "screen" => LogoVariant.Screen, "print" => LogoVariant.Print, _ => null };
                if (parsed is null)
                    return Results.NotFound();
                var content = await service.OpenLogoAsync(logoId, parsed.Value, ct);
                if (content is null)
                    return Results.NotFound();

                http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.ETag = content.ETag;
                if (http.Request.Headers.IfNoneMatch.Any(v => v == content.ETag || v == "*"))
                {
                    await content.File.DisposeAsync();
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                return Results.Stream(content.File.Content, content.File.ContentType, entityTag: new EntityTagHeaderValue(content.ETag));
            })
            .WithTags("Organizations")
            .AllowAnonymous();

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
