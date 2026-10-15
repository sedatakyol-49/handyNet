using HandyNet.Modules.Ledger;
using HandyNet.Modules.Ledger.Contracts;
using HandyNet.Modules.Payments.Application;
using HandyNet.Modules.Payments.Contracts;
using HandyNet.Modules.Payments.Domain;
using HandyNet.Modules.Payments.Infrastructure;
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
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Payments;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddLedgerModule();
        services.AddOptions<PaymentOptions>().Bind(configuration.GetSection(PaymentOptions.Section))
            .Validate(o => !environment.IsProduction() || !o.EnabledProviders.Contains(PaymentProvider.Simulated),
                "The Simulated payment provider must not be enabled in production.")
            .Validate(o => o.CommissionBasisPoints is >= 0 and <= 5000, "Commission must be between 0 and 50 %.")
            .ValidateOnStart();
        services.AddSingleton<IModuleModel, PaymentsModel>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentProcessor>();
        services.AddScoped<ICommissionStatements, CommissionStatements>();
        services.AddSingleton<PaymentRunner>();
        services.AddScoped<IPaymentProvider, SimulatedPaymentProvider>();
        services.AddScoped<IOutboxHandler, OrderStatusPaymentHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders/{orderId:guid}/payments").WithTags("Payments");

        orders.MapPost("/", async (Guid orderId, PaymentService service, CancellationToken ct) =>
                (await service.StartAsync(orderId, ct)).ToHttp(Results.Ok))
            .RequireModule(PlatformModule.B2BPurchasing)
            .RequirePermission(Permissions.OrdersPlace);

        orders.MapGet("/", async (Guid orderId, PaymentService service, CancellationToken ct) =>
                Results.Ok(await service.ListForOrderAsync(orderId, ct)))
            .RequirePermission(Permissions.OrdersRead);

        var payments = app.MapGroup("/api/payments").WithTags("Payments");

        payments.MapGet("/balance", async (PaymentService service, CancellationToken ct) =>
                Results.Ok(await service.GetSellerBalanceAsync(ct)))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        payments.MapGet("/ledger", async (int? page, int? pageSize, ILedger ledger, CancellationToken ct) =>
                Results.Ok(await ledger.GetOwnLinesAsync(page, pageSize, ct)))
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        // Provider webhook (Mollie style: form field "id"). The body is only a hint — the status is fetched from the
        // provider. Unknown ids answer 200 so providers do not retry forever; processing errors answer 500 (retry).
        payments.MapPost("/webhooks/{provider}", async (string provider, [FromForm] string id, PaymentRunner runner, CancellationToken ct) =>
            {
                if (!Enum.TryParse<PaymentProvider>(provider, true, out var parsed) || string.IsNullOrWhiteSpace(id) || id.Length > 100)
                    return Results.BadRequest();
                await runner.SyncByProviderIdAsync(parsed, id, ct);
                return Results.Ok();
            })
            .AllowAnonymous()
            .DisableAntiforgery();

        // Simulated checkout page (development only): lets a developer choose the payment outcome.
        payments.MapGet("/simulated/{providerPaymentId}", (string providerPaymentId) => Results.Content(
                $$"""
                <!doctype html><html lang="de"><meta charset="utf-8"><title>Simulierte Zahlung</title>
                <body style="font-family:system-ui;max-width:32rem;margin:3rem auto">
                <h1>Simulierte Zahlung</h1><p>Keine echte Zahlung. Nur für Entwicklung und Tests.</p>
                <form method="post" action="{{providerPaymentId}}/complete"><button name="outcome" value="Paid">Bezahlen</button>
                <button name="outcome" value="Failed">Fehlschlagen</button> <button name="outcome" value="Canceled">Abbrechen</button></form>
                </body></html>
                """, "text/html"))
            .AllowAnonymous()
            .AddEndpointFilter(SimulatedOnly);

        payments.MapPost("/simulated/{providerPaymentId}/complete", async (string providerPaymentId, [FromForm] ProviderPaymentStatus outcome,
                PaymentRunner runner, IOptions<PaymentOptions> options, CancellationToken ct) =>
            {
                SimulatedPaymentProvider.Complete(providerPaymentId, outcome);
                await runner.SyncByProviderIdAsync(PaymentProvider.Simulated, providerPaymentId, ct);
                return Results.Redirect(SimulatedPaymentProvider.ReturnUrlFor(providerPaymentId)
                                        ?? $"{options.Value.WebBaseUrl.TrimEnd('/')}/app/bestellungen/einkauf");
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .AddEndpointFilter(SimulatedOnly);

        return app;
    }

    private static async ValueTask<object?> SimulatedOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var enabled = services.GetRequiredService<IOptions<PaymentOptions>>().Value.EnabledProviders.Contains(PaymentProvider.Simulated);
        return enabled && !services.GetRequiredService<IHostEnvironment>().IsProduction() ? await next(context) : Results.NotFound();
    }
}
