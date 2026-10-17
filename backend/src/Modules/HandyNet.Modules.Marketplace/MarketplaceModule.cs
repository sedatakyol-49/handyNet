using FluentValidation;
using HandyNet.Modules.Marketplace.Application;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Marketplace;

public static class MarketplaceModule
{
    public static IServiceCollection AddMarketplaceModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, Infrastructure.MarketplaceModel>();
        services.AddScoped<OfferService>();
        services.AddScoped<IOfferSync>(sp => sp.GetRequiredService<OfferService>());
        services.AddScoped<IOfferReservations>(sp => sp.GetRequiredService<OfferService>());
        services.AddValidatorsFromAssemblyContaining<OfferService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder app)
    {
        var offers = app.MapGroup("/api/offers").WithTags("Offers").RequireModule(PlatformModule.B2BSales);

        offers.MapGet("/", async (string? search, string? status, int? page, int? pageSize, OfferService service, CancellationToken ct) =>
            Results.Ok(await service.ListOwnAsync(search, status, page, pageSize, ct)));

        offers.MapPost("/", async (OfferInput input, IValidator<OfferInput> validator, OfferService service, CancellationToken ct) =>
                await validator.ProblemIfInvalidAsync(input, ct) ?? (await service.CreateAsync(input, ct))
                    .ToHttp(o => Results.Created($"/api/offers/{o.Id}", o)))
            .RequirePermission(Permissions.OffersManage);

        offers.MapPut("/{id:guid}", async (Guid id, UpdateOfferRequest request, IValidator<UpdateOfferRequest> validator,
                OfferService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OffersManage);

        var market = app.MapGroup("/api/marketplace").WithTags("Marketplace")
            .RequireModule(PlatformModule.B2BPurchasing)
            .RequirePermission(Permissions.MarketplaceBrowse);

        // Read-only by design: products enter the catalog only via Lager manual entry or import, never here.
        market.MapGet("/offers", async ([AsParameters] BrowseQuery query, IValidator<BrowseQuery> validator, OfferService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(query, ct) ?? Results.Ok(await service.BrowseAsync(query, ct)));

        market.MapGet("/facets", async ([AsParameters] BrowseQuery query, IValidator<BrowseQuery> validator, OfferService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(query, ct) ?? Results.Ok(await service.FacetsAsync(query, ct)));

        market.MapGet("/offers/{id:guid}/detail", async (Guid id, OfferService service, CancellationToken ct) =>
            await service.GetDetailAsync(id, ct) is { } detail
                ? Results.Ok(detail)
                : Problems.From(SharedKernel.Error.NotFound("offer.not_found", "Offer not found.")));

        market.MapGet("/offers/{id:guid}", async Task<Results<Ok<ListingDto>, NotFound>> (Guid id, OfferService service, CancellationToken ct) =>
            (await service.GetListingsAsync([id], ct)).TryGetValue(id, out var listing)
                ? TypedResults.Ok(listing)
                : TypedResults.NotFound());

        return app;
    }
}
