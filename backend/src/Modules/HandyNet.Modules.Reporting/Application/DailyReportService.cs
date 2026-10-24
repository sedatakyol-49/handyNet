using HandyNet.Modules.Ankauf.Contracts;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Payments.Contracts;
using HandyNet.Modules.Pos.Contracts;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Reporting.Application;

/// <summary>B2B purchases (Einkauf, the organization as buyer) of the day.</summary>
public sealed record PurchasesSection(
    IReadOnlyList<OrderDayEvent> Placed,
    long PlacedGrossMinor,
    IReadOnlyList<OrderDayEvent> Received,
    long ReceivedGrossMinor,
    int PaidCount,
    long PaidMinor,
    int RefundedCount,
    long RefundedMinor);

/// <summary>B2B sales (the organization as seller on the marketplace) of the day.</summary>
public sealed record B2BSalesSection(
    IReadOnlyList<OrderDayEvent> Received,
    long ReceivedGrossMinor,
    IReadOnlyList<OrderDayEvent> Shipped,
    long ShippedGrossMinor,
    IReadOnlyList<OrderDayEvent> Completed,
    long CompletedGrossMinor,
    int PaidCount,
    long PaidMinor,
    int RefundedCount,
    long RefundedMinor);

/// <param name="SoldMinor">Till revenue (gross, refunds deducted) + B2B payments received as seller.</param>
/// <param name="BoughtMinor">Ankauf purchases paid + B2B payments made as buyer.</param>
public sealed record DailyTotals(long SoldMinor, long BoughtMinor);

/// <summary>
/// Tagesbericht: what the organization sold and bought on one calendar day (Europe/Berlin), composed from the modules'
/// contracts. A section is null when the organization does not use that module.
/// </summary>
public sealed record DailyReportDto(
    DateOnly Date,
    string TimeZone,
    DateTimeOffset From,
    DateTimeOffset Until,
    DateTimeOffset GeneratedAt,
    string OrganizationName,
    PosDayFigures? Pos,
    AnkaufDayFigures? Ankauf,
    PurchasesSection? Purchases,
    B2BSalesSection? B2BSales,
    DailyTotals Totals);

/// <summary>Calendar days of the reports: Europe/Berlin, so a day can have 23 or 25 hours.</summary>
public static class ReportDay
{
    public const string TimeZoneId = "Europe/Berlin";

    public static readonly TimeZoneInfo Zone = FindZone();

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    /// <summary>Start of a local calendar day as an instant (handles daylight saving: a day has 23–25 hours).</summary>
    public static DateTimeOffset StartOf(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { TimeZoneId, "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Utc;
    }
}

internal sealed class DailyReportService(
    ITenantContext tenant,
    IOrganizationDirectory organizations,
    IPosDailyFigures pos,
    IAnkaufDailyFigures ankauf,
    IOrderDailyFigures orders,
    IPaymentDailyFigures payments,
    IClock clock)
{
    public async Task<Result<DailyReportDto>> GetAsync(DateOnly? date, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var day = date ?? ReportDay.Today(now);
        if (day > ReportDay.Today(now))
            return Error.Validation("reports.date_in_future", "The day lies in the future.");
        if (day.Year < 2000)
            return Error.Validation("reports.date_invalid", "The date is not plausible.");
        var from = ReportDay.StartOf(day);
        var until = ReportDay.StartOf(day.AddDays(1));
        var org = tenant.RequiredOrganizationId;
        var name = (await organizations.GetLegalDetailsAsync(org, ct))?.Name ?? "";

        // Sequential on purpose: the contracts share the request's DbContext.
        var posFigures = tenant.HasModule(PlatformModule.Pos) ? await pos.GetAsync(from, until, ct) : null;
        var ankaufFigures = tenant.HasModule(PlatformModule.Ankauf) ? await ankauf.GetAsync(from, until, ct) : null;
        var buying = tenant.HasModule(PlatformModule.B2BPurchasing);
        var selling = tenant.HasModule(PlatformModule.B2BSales);
        OrderDayFigures? orderFigures = buying || selling ? await orders.GetAsync(from, until, ct) : null;
        PaymentDayFigures? paymentFigures = buying || selling ? await payments.GetAsync(from, until, ct) : null;

        var purchases = buying
            ? new PurchasesSection(orderFigures!.PurchasesPlaced, orderFigures.PurchasesPlaced.Sum(e => e.GrossMinor), orderFigures.PurchasesReceived,
                orderFigures.PurchasesReceived.Sum(e => e.GrossMinor), paymentFigures!.PaidAsBuyerCount, paymentFigures.PaidAsBuyerMinor,
                paymentFigures.RefundedAsBuyerCount, paymentFigures.RefundedAsBuyerMinor)
            : null;
        var sales = selling
            ? new B2BSalesSection(orderFigures!.SalesReceived, orderFigures.SalesReceived.Sum(e => e.GrossMinor), orderFigures.SalesShipped,
                orderFigures.SalesShipped.Sum(e => e.GrossMinor), orderFigures.SalesCompleted, orderFigures.SalesCompleted.Sum(e => e.GrossMinor),
                paymentFigures!.PaidAsSellerCount, paymentFigures.PaidAsSellerMinor, paymentFigures.RefundedAsSellerCount, paymentFigures.RefundedAsSellerMinor)
            : null;

        var totals = new DailyTotals(
            (posFigures?.TotalGrossMinor ?? 0) + (sales?.PaidMinor ?? 0),
            (ankaufFigures?.PaidTotalMinor ?? 0) + (purchases?.PaidMinor ?? 0));
        return new DailyReportDto(day, ReportDay.TimeZoneId, from, until, now, name, posFigures, ankaufFigures, purchases, sales, totals);
    }
}
