namespace HandyNet.Modules.Ordering.Contracts;

/// <summary>One B2B order event in the period (gross amounts in cents).</summary>
public sealed record OrderDayEvent(Guid OrderId, string Number, string Event, string Status, DateTimeOffset At, long GrossMinor);

/// <param name="PurchasesPlaced">Orders the current organization placed as buyer (Einkauf) in the period.</param>
/// <param name="PurchasesReceived">Own purchases delivered in the period (goods received).</param>
/// <param name="SalesReceived">Orders other shops placed with the current organization as seller.</param>
/// <param name="SalesShipped">Own sales handed to the carrier in the period.</param>
/// <param name="SalesCompleted">Own sales completed (inspection window passed / accepted) in the period.</param>
public sealed record OrderDayFigures(
    IReadOnlyList<OrderDayEvent> PurchasesPlaced,
    IReadOnlyList<OrderDayEvent> PurchasesReceived,
    IReadOnlyList<OrderDayEvent> SalesReceived,
    IReadOnlyList<OrderDayEvent> SalesShipped,
    IReadOnlyList<OrderDayEvent> SalesCompleted);

/// <summary>Read API of Ordering for the daily business report (Reporting module); the current tenant's orders only (RLS).</summary>
public interface IOrderDailyFigures
{
    Task<OrderDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);
}
