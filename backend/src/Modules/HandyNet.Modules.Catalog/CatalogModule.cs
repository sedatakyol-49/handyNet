using FluentValidation;
using HandyNet.Modules.Catalog.Application;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.CatalogModel>();
        services.AddScoped<ProductCatalog>();
        services.AddScoped<IProductCatalog>(sp => sp.GetRequiredService<ProductCatalog>());
        services.AddValidatorsFromAssemblyContaining<ProductCatalog>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog");

        group.MapGet("/products", async (string? search, string? category, int? page, int? pageSize,
                ProductCatalog catalog, CancellationToken ct) =>
            Results.Ok(await catalog.SearchAsync(search, category, page, pageSize, ct)))
            .RequireTenant();

        group.MapGet("/products/{id:guid}", async (Guid id, ProductCatalog catalog, CancellationToken ct) =>
                (await catalog.GetAsync([id], ct)).TryGetValue(id, out var product)
                    ? Results.Ok(product)
                    : Results.NotFound())
            .RequireTenant();

        group.MapPost("/products", async (CreateProductRequest request, IValidator<CreateProductRequest> validator,
                ProductCatalog catalog, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await catalog.CreateAsync(request, ct))
                .ToHttp(p => Results.Created($"/api/catalog/products/{p.Id}", p)))
            .RequireModule(PlatformModule.ProductImport)
            .RequirePermission(Permissions.ImportsManage);

        group.MapGet("/categories", () => Results.Ok(new
            {
                categories = Enum.GetNames<ProductCategory>(),
                qualities = Enum.GetNames<PartQuality>(),
            }))
            .RequireAuthorization();

        return app;
    }
}
