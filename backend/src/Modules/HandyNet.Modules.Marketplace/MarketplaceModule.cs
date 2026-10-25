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
        services.AddScoped<OfferModerationService>();
        services.AddScoped<IOfferSync>(sp => sp.GetRequiredService<OfferService>());
        services.AddScoped<IOfferReservations>(sp => sp.GetRequiredService<OfferService>());
        services.AddScoped<IOwnOfferPrices, OwnOfferPrices>();
        services.AddScoped<HandyNet.Modules.Tenancy.Contracts.IOrganizationFactSource, MarketplaceOrganizationFacts>();
        services.AddValidatorsFromAssemblyContaining<OfferService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder app)
    {
        var offers = app.MapGroup("/api/offers").WithTags("Offers").RequireModule(PlatformModule.B2BSales);

        // productId: the own offers of one product (Lager product panel).
        offers.MapGet("/", async (string? search, string? status, Guid? productId, int? page, int? pageSize, OfferService service, CancellationToken ct) =>
            Results.Ok(await service.ListOwnAsync(search, status, page, pageSize, ct, productId)));

        // Platform block of the whole organization as a seller (offers list banner).
        offers.MapGet("/moderation", async (OfferService service, CancellationToken ct) =>
            Results.Ok(await service.GetOwnModerationAsync(ct)));

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

        MapModerationEndpoints(app);
        return app;
    }

    /// <summary>
    /// Platform-admin moderation (no X-Organization-Id): hide single offers, many offers or all offers of a seller from the
    /// marketplace, independent of the seller's own status. Every change is audited (who, when, reason).
    /// </summary>
    private static void MapModerationEndpoints(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/marketplace").WithTags("Marketplace moderation").RequirePlatformAdmin();

        admin.MapGet("/offers", async ([AsParameters] AdminOfferQuery query, IValidator<AdminOfferQuery> validator,
                OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(query, ct) ?? (await service.ListOffersAsync(query, ct)).ToHttp(Results.Ok));

        admin.MapPost("/offers/{id:guid}/block", async (Guid id, ModerationRequest request, IValidator<ModerationRequest> validator,
                OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ModerateOfferAsync(id, true, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/offers/{id:guid}/unblock", async (Guid id, ModerationRequest request, IValidator<ModerationRequest> validator,
                OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ModerateOfferAsync(id, false, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/offers/block", async (BulkModerationRequest request, IValidator<BulkModerationRequest> validator,
                OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.ModerateOffersAsync(request.OfferIds, true, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/offers/unblock", async (BulkModerationRequest request, IValidator<BulkModerationRequest> validator,
                OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.ModerateOffersAsync(request.OfferIds, false, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapGet("/sellers", async (string? search, OfferModerationService service, CancellationToken ct) =>
            (await service.ListSellersAsync(search, ct)).ToHttp(Results.Ok));

        admin.MapPost("/sellers/{organizationId:guid}/block", async (Guid organizationId, ModerationRequest request,
                IValidator<ModerationRequest> validator, OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.ModerateSellerAsync(organizationId, true, request.Reason, ct)).ToHttp(Results.Ok));

        admin.MapPost("/sellers/{organizationId:guid}/unblock", async (Guid organizationId, ModerationRequest request,
                IValidator<ModerationRequest> validator, OfferModerationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.ModerateSellerAsync(organizationId, false, request.Reason, ct)).ToHttp(Results.Ok));
    }
}
