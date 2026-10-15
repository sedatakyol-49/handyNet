using HandyNet.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.SharedKernel.Web;

public static class EndpointFilters
{
    /// <summary>Requires an authenticated user acting for an organization they are a member of.</summary>
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.OrganizationId is null)
                return Problems.From(Error.Forbidden("tenant.missing",
                    $"Header {HandyNetClaims.OrganizationHeader} with an organization you belong to is required."));
            return await next(context);
        });
        return builder;
    }

    /// <summary>Requires a verified organization with the module enabled.</summary>
    public static TBuilder RequireModule<TBuilder>(this TBuilder builder, PlatformModule module)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireTenant();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.OrganizationId is null)
                return await next(context); // RequireTenant filter already produced the response path
            if (!tenant.IsVerified)
                return Problems.From(Error.Forbidden("organization.not_verified", "The organization has not been verified yet."));
            if (!tenant.HasModule(module))
                return Problems.From(Error.Forbidden("module.disabled", $"Module '{module}' is not enabled for this organization."));
            return await next(context);
        });
        return builder;
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireTenant();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.OrganizationId is not null && !tenant.Permissions.Contains(permission))
                return Problems.From(Error.Forbidden("permission.denied", $"Missing permission '{permission}'."));
            return await next(context);
        });
        return builder;
    }

    public static TBuilder RequirePlatformAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(HandyNetClaims.PlatformAdminPolicy);
}
