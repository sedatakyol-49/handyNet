namespace HandyNet.Modules.Payments.Contracts;

/// <summary>Online payments of B2B orders in a period (cents). Buyer = the current organization paid; seller = it was paid.</summary>
public sealed record PaymentDayFigures(
    int PaidAsBuyerCount,
    long PaidAsBuyerMinor,
    int PaidAsSellerCount,
    long PaidAsSellerMinor,
    int RefundedAsBuyerCount,
    long RefundedAsBuyerMinor,
    int RefundedAsSellerCount,
    long RefundedAsSellerMinor);

/// <summary>Read API of Payments for the daily business report (Reporting module); the current tenant's payments only (RLS).</summary>
public interface IPaymentDailyFigures
{
    Task<PaymentDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);
}
