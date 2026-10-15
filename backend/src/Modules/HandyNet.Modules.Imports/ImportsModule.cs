using HandyNet.Modules.Imports.Application;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Imports;

public static class ImportsModule
{
    public static IServiceCollection AddImportsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.ImportsModel>();
        services.AddScoped<ImportService>();
        return services;
    }

    public static IEndpointRouteBuilder MapImportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/imports").WithTags("Imports")
            .RequireModule(PlatformModule.ProductImport)
            .RequirePermission(Permissions.ImportsManage);

        // Upload + dry run. Form fields: file, mapping (JSON object field → column), options (JSON).
        group.MapPost("/", async (IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string? mapping,
                [Microsoft.AspNetCore.Mvc.FromForm] string? options, ImportService service, CancellationToken ct) =>
            {
                var form = ImportService.ParseFormFields(mapping, options);
                if (!form.IsSuccess)
                    return Problems.From(form.Error!);

                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                return (await service.PreviewAsync(file.FileName, buffer.ToArray(), form.Value.Mapping, form.Value.Options, ct))
                    .ToHttp(b => Results.Created($"/api/imports/{b.Id}", b));
            })
            .DisableAntiforgery() // JWT bearer API, no cookies
            .WithMetadata(new RequestSizeLimitAttributeShim(TabularReaders.MaxFileBytes + 64 * 1024));

        group.MapGet("/", async (int? page, int? pageSize, ImportService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(page, pageSize, ct)));

        group.MapGet("/{id:guid}", async (Guid id, ImportService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        group.MapGet("/{id:guid}/rows", async (Guid id, string? status, int? page, int? pageSize, ImportService service, CancellationToken ct) =>
            (await service.GetRowsAsync(id, status, page, pageSize, ct)).ToHttp(Results.Ok));

        group.MapPost("/{id:guid}/commit", async (Guid id, ImportService service, CancellationToken ct) =>
            (await service.CommitAsync(id, ct)).ToHttp(Results.Ok));

        group.MapPost("/{id:guid}/discard", async (Guid id, ImportService service, CancellationToken ct) =>
            (await service.DiscardAsync(id, ct)).ToHttp(Results.Ok));

        group.MapGet("/fields", () => Results.Ok(new
        {
            fields = Domain.ImportField.All,
            required = Domain.ImportField.Required,
        }));

        return app;
    }
}

/// <summary>Per-endpoint request body limit (Kestrel reads IRequestSizeLimitMetadata).</summary>
internal sealed class RequestSizeLimitAttributeShim(long maxBytes) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => maxBytes;
}
