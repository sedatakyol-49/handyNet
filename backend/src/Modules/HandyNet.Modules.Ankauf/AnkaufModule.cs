using FluentValidation;
using HandyNet.Modules.Ankauf.Application;
using HandyNet.Modules.Ankauf.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Ankauf;

public static class AnkaufModule
{
    public static IServiceCollection AddAnkaufModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, AnkaufModel>();
        services.AddScoped<PriceListService>();
        services.AddScoped<QuoteService>();
        services.AddValidatorsFromAssemblyContaining<QuoteService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapAnkaufEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/ankauf").WithTags("Ankauf administration").RequirePlatformAdmin();

        admin.MapPost("/price-lists", async (IFormFile file, [FromForm] string label, [FromForm] DateTimeOffset validFrom,
                [FromForm] DateTimeOffset validUntil, [FromForm] string? sheet, PriceListService service, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                return (await service.UploadAsync(file.FileName, buffer.ToArray(), label, validFrom, validUntil, sheet, ct))
                    .ToHttp(l => Results.Created($"/api/admin/ankauf/price-lists/{l.Id}", l));
            })
            .DisableAntiforgery();

        admin.MapGet("/price-lists", async (PriceListService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        admin.MapGet("/price-lists/{id:guid}", async (Guid id, PriceListService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapGet("/price-lists/{id:guid}/entries", async (Guid id, string? decision, string? change, string? search, int? page,
                int? pageSize, PriceListService service, CancellationToken ct) =>
            (await service.GetEntriesAsync(id, decision, change, search, page, pageSize, ct)).ToHttp(Results.Ok));

        admin.MapPost("/price-lists/{id:guid}/decisions", async (Guid id, DecisionRequest request, PriceListService service, CancellationToken ct) =>
            (await service.DecideAsync(id, request, ct)).ToHttp(Results.Ok));

        admin.MapPost("/price-lists/{id:guid}/publish", async (Guid id, PriceListService service, CancellationToken ct) =>
            (await service.PublishAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapPost("/price-lists/{id:guid}/discard", async (Guid id, PriceListService service, CancellationToken ct) =>
            (await service.DiscardAsync(id, ct)).ToHttp(Results.Ok));

        admin.MapGet("/policies", async (PriceListService service, CancellationToken ct) => Results.Ok(await service.ListPoliciesAsync(ct)));

        admin.MapPost("/policies", async (CreatePolicyRequest request, PriceListService service, CancellationToken ct) =>
            (await service.CreatePolicyAsync(request, ct)).ToHttp(p => Results.Created($"/api/admin/ankauf/policies/{p.Id}", p)));

        var dealer = app.MapGroup("/api/ankauf").WithTags("Ankauf").RequireModule(PlatformModule.Ankauf);

        dealer.MapGet("/models", async (string? search, QuoteService service, CancellationToken ct) =>
            Results.Ok(await service.SearchModelsAsync(search, ct)));

        dealer.MapGet("/quotes", async (QuoteService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        dealer.MapPost("/quotes", async (CreateQuoteRequest request, IValidator<CreateQuoteRequest> validator, QuoteService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.CreateAsync(request, ct))
                .ToHttp(q => Results.Created($"/api/ankauf/quotes/{q.Id}", q)))
            .RequirePermission(Permissions.OrdersPlace);

        return app;
    }
}
