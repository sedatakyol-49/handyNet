using HandyNet.Modules.Ledger.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Payments.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum PaymentProvider
{
    /// <summary>Development only: no real money moves. Refused in Production.</summary>
    Simulated,
    Mollie,
}

public enum PaymentStatus
{
    Open,
    Paid,
    Failed,
    Canceled,
    Expired,
    Refunded,
}

/// <summary>What the provider reports. Webhook bodies are never trusted; status is always fetched from the provider.</summary>
public enum ProviderPaymentStatus
{
    Open,
    Paid,
    Failed,
    Canceled,
    Expired,
}

public enum PaymentChange
{
    None,
    BecamePaid,
    Closed,
}

public sealed record CommissionSplit(long GrossMinor, long CommissionNetMinor, long CommissionVatMinor)
{
    public long SellerShareMinor => GrossMinor - CommissionNetMinor - CommissionVatMinor;

    /// <summary>
    /// Commission is charged on the order's net goods value; the platform adds VAT on its commission (a B2B service
    /// invoiced to the seller). Both are deducted from the seller's payout. Rounding: half away from zero, per amount.
    /// </summary>
    public static CommissionSplit Calculate(long grossMinor, long netGoodsMinor, int commissionBasisPoints, int vatBasisPoints)
    {
        var commission = new Money(netGoodsMinor, Money.Eur).ApplyRate(commissionBasisPoints).MinorUnits;
        var vat = new Money(commission, Money.Eur).ApplyRate(vatBasisPoints).MinorUnits;
        return new CommissionSplit(grossMinor, commission, vat);
    }
}

public sealed class Payment : Entity, IAuditable
{
    private Payment() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }

    /// <summary>Human-readable order number (snapshot) for ledger texts and statements.</summary>
    public string OrderNumber { get; private set; } = null!;
    public Guid BuyerOrganizationId { get; private set; }
    public Guid SellerOrganizationId { get; private set; }
    public PaymentProvider Provider { get; private set; }
    public string ProviderPaymentId { get; private set; } = null!;
    public string? CheckoutUrl { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public int CommissionBasisPoints { get; private set; }
    public long CommissionNetMinor { get; private set; }
    public long CommissionVatMinor { get; private set; }
    public PaymentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }
    public uint Version { get; private set; }

    public CommissionSplit Split => new(AmountMinor, CommissionNetMinor, CommissionVatMinor);

    public static Result<Payment> Create(Guid id, Guid orderId, string orderNumber, Guid buyer, Guid seller, PaymentProvider provider, string providerPaymentId,
        string? checkoutUrl, CommissionSplit split, int commissionBasisPoints, DateTimeOffset now)
    {
        if (split.GrossMinor <= 0 || split.SellerShareMinor < 0)
            return Error.Validation("payment.amount", "Commission cannot exceed the payment amount.");
        return new Payment
        {
            Id = id,
            OrderId = orderId,
            OrderNumber = orderNumber,
            BuyerOrganizationId = buyer,
            SellerOrganizationId = seller,
            Provider = provider,
            ProviderPaymentId = providerPaymentId,
            CheckoutUrl = checkoutUrl,
            AmountMinor = split.GrossMinor,
            Currency = Money.Eur,
            CommissionBasisPoints = commissionBasisPoints,
            CommissionNetMinor = split.CommissionNetMinor,
            CommissionVatMinor = split.CommissionVatMinor,
            Status = PaymentStatus.Open,
            CreatedAt = now,
        };
    }

    /// <summary>Applies the provider's current status. Terminal states never change back (late or replayed webhooks are harmless).</summary>
    public PaymentChange Apply(ProviderPaymentStatus reported, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Open)
            return PaymentChange.None;

        switch (reported)
        {
            case ProviderPaymentStatus.Paid:
                Status = PaymentStatus.Paid;
                PaidAt = now;
                return PaymentChange.BecamePaid;
            case ProviderPaymentStatus.Failed:
                Status = PaymentStatus.Failed;
                return PaymentChange.Closed;
            case ProviderPaymentStatus.Canceled:
                Status = PaymentStatus.Canceled;
                return PaymentChange.Closed;
            case ProviderPaymentStatus.Expired:
                Status = PaymentStatus.Expired;
                return PaymentChange.Closed;
            default:
                return PaymentChange.None;
        }
    }

    public Result MarkRefunded(DateTimeOffset now)
    {
        if (Status != PaymentStatus.Paid)
            return Error.Conflict("payment.not_paid", $"Only paid payments can be refunded (status {Status}).");
        Status = PaymentStatus.Refunded;
        RefundedAt = now;
        return Result.Success();
    }

    public Result CancelOpen()
    {
        if (Status != PaymentStatus.Open)
            return Error.Conflict("payment.not_open", "Only open payments can be canceled.");
        Status = PaymentStatus.Canceled;
        return Result.Success();
    }
}

public sealed class Payout : IAuditable
{
    private Payout() { }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid SellerOrganizationId { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public string ProviderTransferId { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static Payout Create(Payment payment, string providerTransferId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        PaymentId = payment.Id,
        OrderId = payment.OrderId,
        SellerOrganizationId = payment.SellerOrganizationId,
        AmountMinor = payment.Split.SellerShareMinor,
        Currency = payment.Currency,
        ProviderTransferId = providerTransferId,
        CreatedAt = now,
    };
}

/// <summary>The journal entries a payment produces. Debit positive, credit negative; each entry sums to zero.</summary>
public static class PaymentJournal
{
    public static JournalEntryDraft Capture(Payment p, DateTimeOffset at) => new(
        $"payment:{p.Id}:capture", $"Zahlung für Bestellung {p.OrderNumber} eingegangen", "payment", p.Id, at,
        [
            new PostingLine(AccountType.PspClearing, null, p.AmountMinor, p.Currency),
            new PostingLine(AccountType.SellerPayable, p.SellerOrganizationId, -p.Split.SellerShareMinor, p.Currency),
            .. Commission(p, sign: -1),
        ]);

    public static JournalEntryDraft Refund(Payment p, DateTimeOffset at) => new(
        $"payment:{p.Id}:refund", $"Erstattung für Bestellung {p.OrderNumber}", "payment", p.Id, at,
        JournalRules.Reverse(Capture(p, at).Lines));

    public static JournalEntryDraft Payout(Payment p, Guid payoutId, DateTimeOffset at) => new(
        $"payment:{p.Id}:payout", $"Auszahlung an Verkäufer für Bestellung {p.OrderNumber}", "payout", payoutId, at,
        [
            new PostingLine(AccountType.SellerPayable, p.SellerOrganizationId, p.Split.SellerShareMinor, p.Currency),
            new PostingLine(AccountType.PspClearing, null, -p.Split.SellerShareMinor, p.Currency),
        ]);

    private static IEnumerable<PostingLine> Commission(Payment p, int sign)
    {
        if (p.CommissionNetMinor != 0)
            yield return new PostingLine(AccountType.CommissionRevenue, null, sign * p.CommissionNetMinor, p.Currency);
        if (p.CommissionVatMinor != 0)
            yield return new PostingLine(AccountType.VatPayable, null, sign * p.CommissionVatMinor, p.Currency);
    }
}
