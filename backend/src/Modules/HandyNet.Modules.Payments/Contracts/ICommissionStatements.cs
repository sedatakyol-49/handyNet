namespace HandyNet.Modules.Payments.Contracts;

/// <summary>
/// The platform commission (net + VAT) charged on one payment, as snapshotted when the payment was created
/// (ADR-0006). <see cref="RefundedAt"/> is set when the payment was refunded (the commission then falls away).
/// </summary>
public sealed record CommissionCharge(
    Guid PaymentId,
    Guid OrderId,
    string OrderNumber,
    DateTimeOffset PaidAt,
    DateTimeOffset? RefundedAt,
    long CommissionNetMinor,
    long CommissionVatMinor,
    string Currency);

/// <summary>Read API of the Payments module for commission invoicing (Invoicing module).</summary>
public interface ICommissionStatements
{
    /// <summary>
    /// Ids of all sellers with commission activity in [from, until): a payment captured or refunded in that range.
    /// Cross-tenant on purpose (for background jobs and the platform admin); reveals organization ids only, through a
    /// SECURITY DEFINER function (ADR-0005). Read the details as each seller's system actor.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetSellersWithActivityAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>The current tenant's payments as seller captured in [from, until) and still paid (not refunded).</summary>
    Task<IReadOnlyList<CommissionCharge>> GetCapturedAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>The current tenant's payments as seller refunded in [from, until).</summary>
    Task<IReadOnlyList<CommissionCharge>> GetRefundedAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);
}
