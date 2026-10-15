using FluentValidation;
using HandyNet.Modules.Invoicing.Application;
using HandyNet.Modules.Invoicing.Domain;
using HandyNet.Modules.Invoicing.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Invoicing;

public static class InvoicingModule
{
    public static IServiceCollection AddInvoicingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InvoicingOptions>().Bind(configuration.GetSection(InvoicingOptions.Section))
            .Validate(o => o.VatRateBasisPoints is >= 0 and <= 10_000, "Invoicing:VatRateBasisPoints must be between 0 and 10000.")
            .ValidateOnStart();
        services.AddSingleton<IModuleModel, InvoicingModel>();
        services.AddScoped<InvoiceIssuer>();
        services.AddScoped<InvoiceService>();
        services.AddSingleton<InvoiceRunner>();
        services.AddSingleton<InvoiceIssuingWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<InvoiceIssuingWorker>());
        services.AddValidatorsFromAssemblyContaining<InvoiceService>(includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapInvoicingEndpoints(this IEndpointRouteBuilder app)
    {
        // Sellers: their own commission invoices and corrections.
        var seller = app.MapGroup("/api/invoices").WithTags("Invoicing")
            .RequireModule(PlatformModule.B2BSales)
            .RequirePermission(Permissions.OrdersFulfil);

        seller.MapGet("/", async (int? page, int? pageSize, InvoiceService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(page, pageSize, ct)));

        seller.MapGet("/{id:guid}", async (Guid id, InvoiceService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        seller.MapGet("/{id:guid}/pdf", async (Guid id, InvoiceService service, CancellationToken ct) =>
            (await service.GetPdfAsync(id, ct)).ToHttp(f => Results.File(f.Content, "application/pdf", f.FileName)));

        // Platform admin: issue a (completed) month on demand — same idempotent path as the monthly job — and review it.
        var admin = app.MapGroup("/api/admin/invoices").WithTags("Invoicing administration").RequirePlatformAdmin();

        admin.MapPost("/issue", async (IssueInvoicesRequest request, IValidator<IssueInvoicesRequest> validator, InvoiceRunner runner,
                CancellationToken ct) =>
        {
            if (await validator.ProblemIfInvalidAsync(request, ct) is { } invalid)
                return invalid;
            var month = BillingMonth.Parse(request.Month);
            if (!month.IsSuccess)
                return Problems.From(month.Error!);
            return (await runner.IssueMonthAsync(month.Value, ct)).ToHttp(Results.Ok);
        });

        admin.MapGet("/", async (string? month, InvoiceRunner runner, CancellationToken ct) =>
        {
            var parsed = BillingMonth.Parse(month);
            return parsed.IsSuccess ? Results.Ok(await runner.ListMonthAsync(parsed.Value, ct)) : Problems.From(parsed.Error!);
        });

        return app;
    }
}
