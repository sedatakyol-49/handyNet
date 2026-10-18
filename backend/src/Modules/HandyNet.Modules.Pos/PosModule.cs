using FluentValidation;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Contracts;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HandyNet.Modules.Pos;

public static class PosModule
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddPosModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Like the Simulated carrier/payment provider: the test signer (no TSE) is refused at startup in Production.
        services.AddOptions<PosOptions>().Bind(configuration.GetSection(PosOptions.Section))
            .Validate(o => !environment.IsProduction() || o.FiscalSigner != FiscalSignerKind.Disabled,
                "The Disabled fiscal signer (no TSE, test operation) must not be used in production.")
            .Validate(o => o.CashierSessionHours is >= 1 and <= 24, "Pos:CashierSessionHours must be between 1 and 24.")
            .Validate(o => o.ApprovalSeconds is >= 10 and <= 900, "Pos:ApprovalSeconds must be between 10 and 900.")
            .Validate(o => o.PublicReceiptLinkDays is >= 1 and <= 3650, "Pos:PublicReceiptLinkDays must be between 1 and 3650.")
            .ValidateOnStart();
        services.AddSingleton<IModuleModel, PosModel>();
        services.AddScoped<FiscalSignerProvider>();
        services.AddScoped<RegisterService>();
        services.AddScoped<SessionService>();
        services.AddScoped<SaleService>();
        services.AddScoped<PriceResolver>();
        services.AddScoped<ProductService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<ReportService>();
        services.AddScoped<PosActor>();
        services.AddScoped<CashierService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<EReceiptService>();
        // Fallback price source; the host replaces it with one composed from the Marketplace contract.
        services.TryAddScoped<IOfferPriceSource, NoOfferPrices>();
        services.AddValidatorsFromAssemblyContaining<SaleService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder app)
    {
        var pos = app.MapGroup("/api/pos").WithTags("POS").RequireModule(PlatformModule.Pos).ResolvePosActor();
        var sell = pos.MapGroup("").RequirePosPermission(Permissions.PosSell);
        var manage = pos.MapGroup("").RequirePosPermission(Permissions.PosManage);

        // ---- registers ----------------------------------------------------------------------------------------------
        sell.MapGet("/registers", async (RegisterService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        sell.MapGet("/registers/{id:guid}", async (Guid id, RegisterService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapPost("/registers", async (CreateRegisterRequest request, IValidator<CreateRegisterRequest> validator, RegisterService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.CreateAsync(request, ct)).ToHttp(r => Results.Created($"/api/pos/registers/{r.Id}", r)));

        manage.MapPut("/registers/{id:guid}", async (Guid id, UpdateRegisterRequest request, IValidator<UpdateRegisterRequest> validator,
                RegisterService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok));

        // ---- cash sessions ------------------------------------------------------------------------------------------
        sell.MapPost("/registers/{id:guid}/sessions", async (Guid id, OpenSessionRequest request, IValidator<OpenSessionRequest> validator,
                SessionService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.OpenAsync(id, request, ct)).ToHttp(s => Results.Created($"/api/pos/sessions/{s.Id}", s)));

        sell.MapGet("/registers/{id:guid}/sessions/current", async (Guid id, SessionService service, CancellationToken ct) =>
            (await service.GetCurrentAsync(id, ct)).ToHttp(s => s is null ? Results.NoContent() : Results.Ok(s)));

        sell.MapGet("/sessions", async (Guid? registerId, int? page, int? pageSize, SessionService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(registerId, page, pageSize, ct)));

        sell.MapGet("/sessions/{id:guid}", async (Guid id, SessionService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        sell.MapPost("/sessions/{id:guid}/cash-movements", async (Guid id, CashMovementRequest request, IValidator<CashMovementRequest> validator,
                SessionService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.AddMovementAsync(id, request, ct)).ToHttp(m => Results.Created($"/api/pos/sessions/{id}", m)))
            .RequirePosPermission(Permissions.PosCashMovement);

        sell.MapGet("/cash-movement-reasons", () => Results.Ok(SessionService.Reasons()));

        sell.MapPost("/sessions/{id:guid}/close", async (Guid id, CloseSessionRequest request, IValidator<CloseSessionRequest> validator,
                SessionService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.CloseAsync(id, request, ct)).ToHttp(Results.Ok))
            .RequirePosPermission(Permissions.PosCloseSession);

        // ---- products and prices ------------------------------------------------------------------------------------
        sell.MapGet("/registers/{id:guid}/products", async (Guid id, string? search, string? category, bool? includeOutOfStock, int? page,
                int? pageSize, ProductService service, CancellationToken ct) =>
            (await service.ListAsync(id, search, category, includeOutOfStock ?? false, page, pageSize, ct)).ToHttp(Results.Ok));

        sell.MapGet("/registers/{id:guid}/product-categories", async (Guid id, ProductService service, CancellationToken ct) =>
            (await service.CategoriesAsync(id, ct)).ToHttp(Results.Ok));

        sell.MapGet("/prices/{productId:guid}", async (Guid productId, Guid? registerId, ProductService service, CancellationToken ct) =>
            (await service.GetPriceAsync(productId, registerId, ct)).ToHttp(Results.Ok));

        manage.MapPut("/prices/{productId:guid}", async (Guid productId, SetPosPriceRequest request, IValidator<SetPosPriceRequest> validator,
                ProductService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SetPriceAsync(productId, request, ct)).ToHttp(Results.Ok));

        // ---- customers ----------------------------------------------------------------------------------------------
        sell.MapGet("/customers", async (string? search, int? page, int? pageSize, CustomerService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(search, page, pageSize, ct)));

        sell.MapGet("/customers/{id:guid}", async (Guid id, CustomerService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        sell.MapPost("/customers", async (CustomerRequest request, IValidator<CustomerRequest> validator, CustomerService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.CreateAsync(request, ct)).ToHttp(c => Results.Created($"/api/pos/customers/{c.Id}", c)));

        sell.MapPut("/customers/{id:guid}", async (Guid id, CustomerRequest request, IValidator<CustomerRequest> validator, CustomerService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok));

        // ---- reports ------------------------------------------------------------------------------------------------
        sell.MapGet("/reports/sales", async (Guid? sessionId, Guid? registerId, DateTimeOffset? from, DateTimeOffset? to, ReportService service,
                CancellationToken ct) =>
            (await service.SalesAsync(sessionId, registerId, from, to, ct)).ToHttp(Results.Ok));

        // ---- sales, storno, returns, lookup -------------------------------------------------------------------------
        sell.MapPost("/sales", async (CreateSaleRequest request, [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
                IValidator<CreateSaleRequest> validator, SaleService service, CancellationToken ct) =>
            {
                if (InvalidKey(idempotencyKey) is { } problem)
                    return problem;
                return await validator.ProblemIfInvalidAsync(request, ct)
                       ?? (await service.CreateSaleAsync(request, idempotencyKey!.Trim(), ct)).ToHttp(r => r.Replayed
                           ? Results.Ok(r.Receipt)
                           : Results.Created($"/api/pos/sales/{r.Receipt.Id}", r.Receipt));
            });

        sell.MapGet("/sales", async ([AsParameters] SaleQuery query, SaleService service, CancellationToken ct) =>
            (await service.ListAsync(query, ct)).ToHttp(Results.Ok));

        sell.MapGet("/sales/{id:guid}", async (Guid id, SaleService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        sell.MapPost("/sales/{id:guid}/reprints", async (Guid id, SaleService service, CancellationToken ct) =>
            (await service.ReprintAsync(id, ct)).ToHttp(r => Results.Created($"/api/pos/sales/{id}", r)));

        manage.MapPost("/sales/{id:guid}/storno", async (Guid id, StornoRequest request, IValidator<StornoRequest> validator, SaleService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.StornoAsync(id, request, ct)).ToHttp(r => Results.Created($"/api/pos/sales/{r.Id}", r)));

        sell.MapPost("/sales/{id:guid}/returns", async (Guid id, ReturnRequest request, [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
                IValidator<ReturnRequest> validator, SaleService service, CancellationToken ct) =>
            {
                if (InvalidKey(idempotencyKey) is { } problem)
                    return problem;
                return await validator.ProblemIfInvalidAsync(request, ct)
                       ?? (await service.ReturnAsync(id, request, idempotencyKey!.Trim(), ct)).ToHttp(r => r.Replayed
                           ? Results.Ok(r.Receipt)
                           : Results.Created($"/api/pos/sales/{r.Receipt.Id}", r.Receipt));
            })
            .RequirePosPermission(Permissions.PosRefund);

        sell.MapGet("/lookup", async (string? code, Guid? registerId, SaleService service, CancellationToken ct) =>
            (await service.LookupAsync(code, registerId, ct)).ToHttp(Results.Ok));

        MapCashierEndpoints(pos, sell);
        MapDocumentEndpoints(app, sell);
        return app;
    }

    /// <summary>Rate limit for PIN entry (sign-in, approvals, setting the own PIN with the password): per user and minute.</summary>
    public const string PinRateLimitPolicy = "pos-pin";

    // ---- PINs and cashier switching on a shared device (ADR-0008 "Staff: PIN, cashier switching, approvals") ----------------
    private static void MapCashierEndpoints(RouteGroupBuilder pos, RouteGroupBuilder sell)
    {
        sell.MapGet("/me/pin", async (CashierService service, CancellationToken ct) => Results.Ok(await service.GetOwnPinAsync(ct)));

        sell.MapPut("/me/pin", async (SetOwnPinRequest request, IValidator<SetOwnPinRequest> validator, CashierService service, CancellationToken ct) =>
                await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SetOwnPinAsync(request, ct)).ToHttp(Results.Ok))
            .WithMetadata(new IgnoreCashierTokenMetadata())
            .RequireRateLimiting(PinRateLimitPolicy);

        pos.MapGet("/pins", async (CashierService service, CancellationToken ct) => Results.Ok(await service.ListPinsAsync(ct)))
            .RequirePermission(Permissions.OrganizationManage).WithMetadata(new IgnoreCashierTokenMetadata());

        pos.MapPut("/cashiers/{userId:guid}/pin", async (Guid userId, SetPinRequest request, IValidator<SetPinRequest> validator, CashierService service,
                CancellationToken ct) =>
                await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SetPinForAsync(userId, request, ct)).ToHttp(Results.Ok))
            .RequirePermission(Permissions.OrganizationManage).WithMetadata(new IgnoreCashierTokenMetadata());

        sell.MapGet("/cashiers", async (CashierService service, CancellationToken ct) => Results.Ok(await service.ListCashiersAsync(ct)));

        sell.MapPost("/registers/{id:guid}/cashier", async (Guid id, CashierSignInRequest request, IValidator<CashierSignInRequest> validator,
                CashierService service, CancellationToken ct) =>
                await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SignInAsync(id, request, ct)).ToHttp(Results.Ok))
            .WithMetadata(new IgnoreCashierTokenMetadata())
            .RequireRateLimiting(PinRateLimitPolicy);

        // Manager approval by PIN for the cashier's next action on this register (header X-Pos-Approval, single use).
        sell.MapPost("/registers/{id:guid}/approvals", async (Guid id, ApprovalRequest request, IValidator<ApprovalRequest> validator, PosActor actor,
                ApprovalService service, CancellationToken ct) =>
                await validator.ProblemIfInvalidAsync(request, ct)
                ?? (await service.GrantAsync(id, request, actor, ct)).ToHttp(a => Results.Created($"/api/pos/registers/{id}", a)))
            .RequireRateLimiting(PinRateLimitPolicy);

        sell.MapGet("/cashier", async (PosActor actor, CashierService service, CancellationToken ct) =>
            (await service.CurrentAsync(actor, ct)).ToHttp(Results.Ok));

        sell.MapPost("/cashier/lock", async (HttpRequest http, CashierService service, CancellationToken ct) =>
            {
                await service.LockAsync(http.Headers[PosActor.CashierHeader].ToString().Trim(), ct);
                return Results.NoContent();
            })
            .WithMetadata(new IgnoreCashierTokenMetadata());
    }

    /// <summary>Anonymous public receipt view (QR link): per client IP and minute.</summary>
    public const string PublicReceiptRateLimitPolicy = "pos-public-receipt";

    // ---- e-receipts and invoices for POS sales -----------------------------------------------------------------------------
    private static void MapDocumentEndpoints(IEndpointRouteBuilder app, RouteGroupBuilder sell)
    {
        sell.MapPost("/sales/{id:guid}/email", async (Guid id, EmailReceiptRequest request, IValidator<EmailReceiptRequest> validator,
                EReceiptService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.EmailAsync(id, request, ct)).ToHttp(r => Results.Accepted(null, r)));

        sell.MapPost("/sales/{id:guid}/public-link", async (Guid id, EReceiptService service, CancellationToken ct) =>
            (await service.CreateLinkAsync(id, ct)).ToHttp(r => Results.Created(r.Path, r)));

        app.MapGet("/api/public/receipts/{token}", async (string token, EReceiptService service, CancellationToken ct) =>
                (await service.GetPublicAsync(token, ct)).ToHttp(Results.Ok))
            .WithTags("POS")
            .AllowAnonymous()
            .RequireRateLimiting(PublicReceiptRateLimitPolicy);
    }

    private static IResult? InvalidKey(string? key) =>
        string.IsNullOrWhiteSpace(key) || key.Trim().Length > SaleService.MaxIdempotencyKeyLength
            ? Problems.From(Error.Validation("pos.idempotency_key",
                $"Header {IdempotencyHeader} is required (max {SaleService.MaxIdempotencyKeyLength} characters)."))
            : null;
}
