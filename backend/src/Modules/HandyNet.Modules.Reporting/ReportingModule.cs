using HandyNet.Modules.Reporting.Application;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Reporting;

/// <summary>
/// Cross-module business reports (ADR-0016). Owns no data: it composes the read contracts of POS, Ankauf, Ordering and
/// Payments under the caller's RLS. Nothing depends on it (architecture tests).
/// </summary>
public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddScoped<DailyReportService>();
        return services;
    }

    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/reports").WithTags("Reports").RequireTenant().RequirePermission(Permissions.ReportsRead);

        reports.MapGet("/daily", async (DateOnly? date, DailyReportService service, CancellationToken ct) =>
            (await service.GetAsync(date, ct)).ToHttp(Results.Ok));

        return app;
    }
}
