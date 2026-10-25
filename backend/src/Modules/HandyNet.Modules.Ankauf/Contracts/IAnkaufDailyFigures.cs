namespace HandyNet.Modules.Ankauf.Contracts;

public sealed record AnkaufPaymentFigure(string Method, int Count, long AmountMinor);

/// <summary>A device bought in the period (no seller data: the report is about the business, not the person).</summary>
public sealed record AnkaufDeviceFigure(Guid IntakeId, DateTimeOffset CompletedAt, string Brand, string Model, string? Storage, string Imei,
    string? Condition, long PaidMinor, string PaymentMethod, bool PaidFromTill);

public sealed record AnkaufDayFigures(
    int ReceivedCount,
    int CompletedCount,
    int RejectedCount,
    long PaidTotalMinor,
    IReadOnlyList<AnkaufPaymentFigure> ByPaymentMethod,
    IReadOnlyList<AnkaufDeviceFigure> Devices);

/// <summary>Read API of Ankauf for the daily business report (Reporting module); the current tenant's intakes only (RLS).</summary>
public interface IAnkaufDailyFigures
{
    Task<AnkaufDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);
}
