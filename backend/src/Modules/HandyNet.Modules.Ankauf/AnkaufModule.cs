using FluentValidation;
using HandyNet.Modules.Ankauf.Application;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Ankauf.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HandyNet.Modules.Ankauf;

public static class AnkaufModule
{
    public static IServiceCollection AddAnkaufModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<AnkaufOptions>().Bind(configuration.GetSection(AnkaufOptions.Section))
            .Validate(o => o.SellerDataRetentionYears is null or (>= 1 and <= 30), "Ankauf:SellerDataRetentionYears must be between 1 and 30 (or null).")
            .Validate(o => o.CheckInMinutes is >= 1 and <= 120, "Ankauf:CheckInMinutes must be between 1 and 120.")
            .Validate(o => o.CheckInPurgeHours is >= 1 and <= 168, "Ankauf:CheckInPurgeHours must be between 1 and 168.")
            .ValidateOnStart();
        services.AddSingleton<SellerDataRetention>();
        services.AddHostedService(sp => sp.GetRequiredService<SellerDataRetention>());
        services.AddSingleton<IModuleModel, AnkaufModel>();
        services.AddScoped<PriceListService>();
        services.AddScoped<QuoteService>();
        services.AddScoped<IntakeService>();
        services.AddScoped<CheckInService>();
        services.AddScoped<Contracts.IAnkaufDailyFigures, AnkaufDailyFigures>();
        services.AddValidatorsFromAssemblyContaining<QuoteService>(includeInternalTypes: true);
        return services;
    }

    /// <summary>Anonymous QR self check-in form: per client IP and minute.</summary>
    public const string CheckInRateLimitPolicy = "ankauf-checkin";

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

        var intakes = dealer.MapGroup("/intakes").RequirePermission(Permissions.InventoryManage);

        intakes.MapGet("/", async (IntakeService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        intakes.MapGet("/{id:guid}", async (Guid id, IntakeService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        intakes.MapGet("/{id:guid}/beleg", async (Guid id, IntakeService service, CancellationToken ct) =>
            (await service.GetBelegAsync(id, ct)).ToHttp(f => Results.File(f.Content, "application/pdf", f.FileName)));

        intakes.MapPost("/", async (ReceiveIntakeRequest request, IValidator<ReceiveIntakeRequest> validator, IntakeService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ReceiveAsync(request, ct))
                .ToHttp(i => Results.Created($"/api/ankauf/intakes/{i.Id}", i)));

        intakes.MapPost("/{id:guid}/inspect", async (Guid id, InspectIntakeRequest request, IntakeService service, CancellationToken ct) =>
            (await service.InspectAsync(id, request, ct)).ToHttp(Results.Ok));

        intakes.MapPost("/{id:guid}/complete", async (Guid id, CompleteIntakeRequest request, IntakeService service, CancellationToken ct) =>
            (await service.CompleteAsync(id, request, ct)).ToHttp(Results.Ok));

        intakes.MapPost("/{id:guid}/reject", async (Guid id, RejectIntakeRequest request, IntakeService service, CancellationToken ct) =>
            (await service.RejectAsync(id, request.Reason, ct)).ToHttp(Results.Ok));

        // ---- seller signature (bound to data + price, ADR-0014) ------------------------------------------------------------
        intakes.MapPost("/{id:guid}/signature", async (Guid id, SignIntakeRequest request, IValidator<SignIntakeRequest> validator, IntakeService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SignAsync(id, request, ct)).ToHttp(Results.Ok))
            .WithMetadata(new RequestBodyLimit(SignatureImage.MaxBytes * 2));

        intakes.MapGet("/{id:guid}/signatures/{signatureId:guid}/image", async (Guid id, Guid signatureId, HttpResponse response, IntakeService service,
                CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                return (await service.GetSignatureImageAsync(id, signatureId, ct)).ToHttp(png => Results.File(png, "image/png"));
            });

        // ---- returning sellers and tills for the payout --------------------------------------------------------------------
        var staff = dealer.MapGroup("").RequirePermission(Permissions.InventoryManage);
        staff.MapGet("/sellers", async (string? search, IntakeService service, CancellationToken ct) =>
            Results.Ok(await service.SearchSellersAsync(search, ct)));

        staff.MapGet("/payout-registers", async (IntakeService service, CancellationToken ct) => Results.Ok(await service.PayoutRegistersAsync(ct)));

        // ---- QR self check-in (employee side) ------------------------------------------------------------------------------
        staff.MapPost("/checkins", async (CheckInService service, CancellationToken ct) =>
            (await service.CreateAsync(ct)).ToHttp(c => Results.Created($"/api/ankauf/checkins/{c.Id}", c)));

        staff.MapGet("/checkins/{id:guid}", async (Guid id, HttpResponse response, CheckInService service, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                return (await service.GetAsync(id, ct)).ToHttp(Results.Ok);
            });

        staff.MapGet("/checkins/{id:guid}/signature", async (Guid id, HttpResponse response, CheckInService service, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                return (await service.GetSignatureAsync(id, ct)).ToHttp(png => Results.File(png, "image/png"));
            });

        // ---- QR self check-in (public, anonymous, rate-limited; only by the unguessable token) -----------------------------
        var checkin = app.MapGroup("/api/public/ankauf/checkins").WithTags("Ankauf self check-in").AllowAnonymous()
            .RequireRateLimiting(CheckInRateLimitPolicy);

        checkin.MapGet("/{token}", async (string token, HttpResponse response, CheckInService service, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                return (await service.GetPublicAsync(token, ct)).ToHttp(Results.Ok);
            });

        checkin.MapPost("/{token}", async (string token, PublicCheckInRequest request, IValidator<PublicCheckInRequest> validator, CheckInService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SubmitPublicAsync(token, request, ct)).ToHttp(Results.NoContent))
            .WithMetadata(new RequestBodyLimit(SignatureImage.MaxBytes * 2));

        return app;
    }
}
