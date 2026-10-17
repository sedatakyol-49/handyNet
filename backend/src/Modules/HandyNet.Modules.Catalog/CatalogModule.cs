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
using Microsoft.Net.Http.Headers;

namespace HandyNet.Modules.Catalog;

public static class CatalogModule
{
    /// <summary>Multipart overhead allowed on top of the 5 MB image limit before Kestrel rejects the body.</summary>
    private const long UploadEnvelopeBytes = 256 * 1024;

    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.CatalogModel>();
        services.AddScoped<ProductCatalog>();
        services.AddScoped<IProductCatalog>(sp => sp.GetRequiredService<ProductCatalog>());
        services.AddSingleton<IImageProcessor, Infrastructure.SkiaImageProcessor>();
        services.AddScoped<ProductImageService>();
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
                // Categories stocked by IMEI (one per unit): required for smartphones, optional for tablets/wearables.
                imeiRequired = Enum.GetValues<ProductCategory>().Where(SerializedStock.RequiresImei).Select(c => c.ToString()),
                imeiAllowed = Enum.GetValues<ProductCategory>().Where(SerializedStock.AllowsImei).Select(c => c.ToString()),
            }))
            .RequireAuthorization();

        // ---- product images ---------------------------------------------------------------------------------------
        group.MapGet("/products/{id:guid}/images", async (Guid id, ProductImageService images, CancellationToken ct) =>
                (await images.ListAsync(id, ct)).ToHttp(Results.Ok))
            .RequireTenant();

        // Tenant: the organization that created the product. Admin variant below: platform administrators, no tenant.
        var tenantImages = group.MapGroup("/products/{id:guid}/images")
            .RequireModule(PlatformModule.ProductImport)
            .RequirePermission(Permissions.ImportsManage);
        MapImageCommands(tenantImages, asAdmin: false);

        var adminImages = app.MapGroup("/api/admin/catalog/products/{id:guid}/images").WithTags("Catalog administration")
            .RequirePlatformAdmin();
        MapImageCommands(adminImages, asAdmin: true);

        // Public on purpose (plain <img> tags, future B2C storefront): canonical catalog content behind random ids, immutable.
        group.MapGet("/images/{imageId:guid}/{variant}", async (Guid imageId, string variant, HttpContext http,
                ProductImageService images, CancellationToken ct) =>
            {
                ImageVariant? parsed = variant switch { "thumb" => ImageVariant.Thumbnail, "full" => ImageVariant.Full, _ => null };
                if (parsed is null)
                    return Results.NotFound();
                var content = await images.OpenAsync(imageId, parsed.Value, ct);
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
            .AllowAnonymous();

        return app;
    }

    private static void MapImageCommands(RouteGroupBuilder images, bool asAdmin)
    {
        // Multipart form field "file". JPEG/PNG/WebP up to 5 MB, verified by magic bytes, re-encoded to WebP.
        images.MapPost("/", async (Guid id, IFormFile? file, ProductImageService service, CancellationToken ct) =>
            {
                if (file is null)
                    return Problems.From(SharedKernel.Error.Validation("image.file_missing", "Send the image as multipart form field 'file'."));
                if (ImageLimits.CheckUpload(file.Length) is { } sizeError)
                    return Problems.From(sizeError);
                using var buffer = new MemoryStream((int)file.Length);
                await using (var stream = file.OpenReadStream())
                    await stream.CopyToAsync(buffer, ct);
                return (await service.UploadAsync(id, buffer.ToArray(), asAdmin, ct))
                    .ToHttp(i => Results.Created(i.FullUrl, i));
            })
            .DisableAntiforgery() // JWT bearer API, no cookies
            .WithMetadata(new RequestBodyLimit(ImageLimits.MaxUploadBytes + UploadEnvelopeBytes));

        images.MapPut("/order", async (Guid id, ReorderImagesRequest request, ProductImageService service, CancellationToken ct) =>
            (await service.ReorderAsync(id, request, asAdmin, ct)).ToHttp(Results.Ok));

        images.MapDelete("/{imageId:guid}", async (Guid id, Guid imageId, ProductImageService service, CancellationToken ct) =>
            (await service.RemoveAsync(id, imageId, asAdmin, ct)).ToHttp(Results.NoContent));
    }
}
