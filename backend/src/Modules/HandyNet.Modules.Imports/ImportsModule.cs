using FluentValidation;
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
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddImportsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.ImportsModel>();
        services.AddScoped<ImportService>();
        services.AddScoped<ManualEntryService>();
        services.AddValidatorsFromAssemblyContaining<ManualEntryService>(includeInternalTypes: true);
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
            .WithMetadata(new RequestBodyLimit(TabularReaders.MaxFileBytes + 64 * 1024));

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

        // ---- manual single-item entry in Lager ("Artikel anlegen") ---------------------------------------------------
        // Mapped under /api/inventory because it is the Lager screen's command; Imports owns it because it orchestrates
        // Catalog + Inventory + Marketplace exactly like a one-row import. Creating a product additionally needs
        // ProductImport + imports.manage, an offer B2BSales + offers.manage (checked in the service, 403).
        var items = app.MapGroup("/api/inventory/items").WithTags("Inventory")
            .RequireModule(PlatformModule.Inventory)
            .RequirePermission(Permissions.InventoryManage);

        items.MapPost("/", async (ManualEntryRequest request, [Microsoft.AspNetCore.Mvc.FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
                IValidator<ManualEntryRequest> validator, ManualEntryService service, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > ManualEntryService.MaxIdempotencyKeyLength)
                    return Problems.From(SharedKernel.Error.Validation("entry.idempotency_key",
                        $"Header {IdempotencyHeader} is required (max {ManualEntryService.MaxIdempotencyKeyLength} characters)."));
                return await validator.ProblemIfInvalidAsync(request, ct)
                       ?? (await service.CreateAsync(request, idempotencyKey.Trim(), ct)).ToHttp(r => r.Replayed
                           ? Results.Ok(r.Entry)
                           : Results.Created($"/api/inventory/items/{r.Entry.EntryId}", r.Entry));
            });

        items.MapGet("/lookup", async (string? q, ManualEntryService service, CancellationToken ct) =>
            Results.Ok(await service.LookupAsync(q, ct)));

        return app;
    }
}

