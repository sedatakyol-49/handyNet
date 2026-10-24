using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

public enum IntakeStatus
{
    /// <summary>Device and seller details recorded; nothing paid yet.</summary>
    Received,

    /// <summary>Physically checked; the final offer was recalculated with the quote's price version and policy.</summary>
    Inspected,

    /// <summary>Seller paid and the device entered stock (one transaction).</summary>
    Completed,

    /// <summary>Not bought (device returned to the seller).</summary>
    Rejected,
}

public enum PaymentMethod
{
    Cash,
    BankTransfer,
}

/// <summary>
/// The physical purchase of a used device from a private seller, based on a preliminary quote. The final price is
/// recalculated after inspection with the quote's own price version and policy (never today's prices), and the
/// dealer may pay at most the inspected platform offer. Completing it records the payment and stocks the IMEI.
/// </summary>
public sealed class Intake : IAuditable
{
    public const int MinimumSellerAge = SellerRules.MinimumAge;
    public const int MaxNotesLength = 2000;
    public const int MaxReasonLength = 1000;

    private Intake() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid QuoteId { get; private set; }
    public string Imei { get; private set; } = null!;
    [PersonalData]
    public SellerDetails Seller { get; private set; } = null!;

    /// <summary>When the seller's personal data was replaced by <see cref="SellerDetails.Anonymized"/> (retention job).</summary>
    public DateTimeOffset? AnonymizedAt { get; private set; }

    public bool IsAnonymized => AnonymizedAt is not null;

    public IntakeStatus Status { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public Guid ReceivedBy { get; private set; }

    /// <summary>How the seller's identity data was captured (manual, MRZ, self check-in).</summary>
    public SellerCaptureMethod CaptureMethod { get; private set; }

    /// <summary>The self check-in the seller data came from, if any.</summary>
    public Guid? CheckInId { get; private set; }

    public DeviceCondition? InspectedCondition { get; private set; }
    public List<DeviceDefect> InspectedDefects { get; private set; } = [];
    public int? InspectedBatteryHealthPercent { get; private set; }
    public string? InspectionNotes { get; private set; }
    public long? InspectedOfferMinor { get; private set; }
    public long? InspectedMaxConsumerPriceMinor { get; private set; }
    public DateTimeOffset? InspectedAt { get; private set; }

    public long? PaidMinor { get; private set; }
    public PaymentMethod? PaymentMethod { get; private set; }
    public Guid? StockUnitId { get; private set; }

    /// <summary>The seller's purchase signature that was valid when the intake was completed (null if signing was not required).</summary>
    public Guid? SignatureId { get; private set; }

    /// <summary>Cash payout from a till (POS cash movement) for cash purchases of organizations using the POS module.</summary>
    public Guid? CashMovementId { get; private set; }

    public Guid? CashRegisterId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? RejectionReason { get; private set; }
    public uint Version { get; private set; }

    public static Result<Intake> Receive(Guid organizationId, Quote quote, string imei, SellerDetails seller, Guid userId, DateTimeOffset now,
        SellerCaptureMethod captureMethod = SellerCaptureMethod.Manual, Guid? checkInId = null)
    {
        if (quote.OrganizationId != organizationId)
            return Error.NotFound("ankauf.quote_not_found", "Quote not found.");
        if (quote.ValidUntil <= now)
            return Error.Conflict("ankauf.quote_expired", "The quote has expired. Create a new quote with current prices.");
        if (!Domain.Imei.TryNormalize(imei, out var normalized))
            return Error.Validation("ankauf.imei", "IMEI must be 15 digits with a valid check digit.");
        if (quote.Imei is { } quoted && quoted != normalized)
            return Error.Validation("ankauf.imei_mismatch", "The IMEI differs from the one in the quote.");
        var checkedSeller = SellerRules.Validate(seller, Today(now));
        if (!checkedSeller.IsSuccess)
            return checkedSeller.Error!;
        if (captureMethod == SellerCaptureMethod.SelfCheckIn && checkInId is null)
            throw new ArgumentException("A self check-in intake names its check-in.", nameof(checkInId));

        return new Intake
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            QuoteId = quote.Id,
            Imei = normalized,
            Seller = checkedSeller.Value,
            Status = IntakeStatus.Received,
            ReceivedAt = now,
            ReceivedBy = userId,
            CaptureMethod = captureMethod,
            CheckInId = checkInId,
        };
    }

    /// <summary>Calendar day of an instant in German local time (age and ID expiry are judged on the shop's day).</summary>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Ankaufbeleg.GermanTimeZone).DateTime);

    /// <summary>The amount paid must be positive and at most the inspected offer (otherwise the device is sold at a loss).</summary>
    public static Error? CheckPaidAmount(long paidMinor, long? inspectedOfferMinor)
    {
        if (paidMinor <= 0)
            return Error.Validation("ankauf.paid_amount", "The amount paid must be positive.");
        if (inspectedOfferMinor is null)
            return Error.Conflict("ankauf.not_inspected", "Inspect the device before paying the seller.");
        if (paidMinor > inspectedOfferMinor)
            return Error.Validation("ankauf.paid_above_offer",
                $"The amount exceeds the inspected offer ({inspectedOfferMinor / 100m:0.00} €) and would be sold at a loss.");
        return null;
    }

    /// <summary>Intakes whose retention start lies before this instant are due for anonymization.</summary>
    public static DateTimeOffset RetentionCutoff(DateTimeOffset now, int retentionYears) =>
        retentionYears >= 1 ? now.AddYears(-retentionYears) : throw new ArgumentOutOfRangeException(nameof(retentionYears));

    /// <summary>The date from which the retention period runs: completion/rejection, or receipt for intakes never closed.</summary>
    public DateTimeOffset RetentionStart => CompletedAt ?? ReceivedAt;

    /// <summary>
    /// Replaces the seller's personal data with a fixed placeholder once the retention period is over. Everything else
    /// (device, amounts, payment method, stock unit) stays for bookkeeping. Idempotent.
    /// </summary>
    public Result AnonymizeSeller(DateTimeOffset cutoff, DateTimeOffset now)
    {
        if (IsAnonymized)
            return Result.Success();
        if (RetentionStart >= cutoff)
            return Error.Conflict("ankauf.retention_not_over", "The retention period of this intake is not over.");
        Seller = SellerDetails.Anonymized;
        AnonymizedAt = now;
        return Result.Success();
    }

    public Result Inspect(QuoteInput findings, QuoteCalculation calculation, string? notes, DateTimeOffset now)
    {
        if (IsAnonymized)
            return Error.Conflict("ankauf.intake_anonymized", "The seller data of this intake was anonymized; it cannot be continued.");
        if (Status is not (IntakeStatus.Received or IntakeStatus.Inspected))
            return Error.Conflict("ankauf.intake_closed", $"An intake in status {Status} cannot be inspected.");
        if (notes?.Length > MaxNotesLength)
            return Error.Validation("ankauf.notes", $"Inspection notes are limited to {MaxNotesLength} characters.");
        InspectedCondition = findings.Condition;
        InspectedDefects = findings.Defects.Distinct().Order().ToList();
        InspectedBatteryHealthPercent = findings.BatteryHealthPercent;
        InspectionNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        InspectedOfferMinor = calculation.PlatformOfferMinor;
        InspectedMaxConsumerPriceMinor = calculation.MaxConsumerPriceMinor;
        InspectedAt = now;
        Status = IntakeStatus.Inspected;
        return Result.Success();
    }

    /// <summary>
    /// Checks everything that must hold before money changes hands (no side effects): open, inspected, amount within the
    /// offer, and — if required — a purchase signature that confirms exactly this intake, amount and payment method.
    /// </summary>
    public Error? CheckPayable(Quote quote, long paidMinor, PaymentMethod method, IntakeSignature? signature, bool signatureRequired)
    {
        if (IsAnonymized)
            return Error.Conflict("ankauf.intake_anonymized", "The seller data of this intake was anonymized; it cannot be continued.");
        if (Status != IntakeStatus.Inspected)
            return Error.Conflict("ankauf.not_inspected", "Inspect the device before paying the seller.");
        if (CheckPaidAmount(paidMinor, InspectedOfferMinor) is { } amountError)
            return amountError;
        if (signature is not null && !signature.Confirms(this, quote, paidMinor, method))
            return Error.Conflict("ankauf.signature_invalid",
                "The seller's signature does not match the current data, amount or payment method. The seller must sign again.");
        if (signature is null && signatureRequired)
            return Error.Conflict("ankauf.signature_required", "The seller must sign the purchase (declaration of ownership and price) before payment.");
        return null;
    }

    public Result Complete(long paidMinor, PaymentMethod method, Guid stockUnitId, DateTimeOffset now, Quote? quote = null, IntakeSignature? signature = null,
        bool signatureRequired = false, (Guid MovementId, Guid RegisterId)? cashPayout = null)
    {
        if (quote is null && (signature is not null || signatureRequired))
            throw new ArgumentNullException(nameof(quote));
        if (CheckPayable(quote!, paidMinor, method, signature, signatureRequired) is { } error)
            return error;
        if (cashPayout is not null && method != Domain.PaymentMethod.Cash)
            throw new ArgumentException("Only cash purchases are paid out of a till.", nameof(cashPayout));
        PaidMinor = paidMinor;
        PaymentMethod = method;
        StockUnitId = stockUnitId;
        SignatureId = signature?.Id;
        CashMovementId = cashPayout?.MovementId;
        CashRegisterId = cashPayout?.RegisterId;
        CompletedAt = now;
        Status = IntakeStatus.Completed;
        return Result.Success();
    }

    public Result Reject(string reason, DateTimeOffset now)
    {
        if (Status is IntakeStatus.Completed or IntakeStatus.Rejected)
            return Error.Conflict("ankauf.intake_closed", $"An intake in status {Status} cannot be rejected.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
            return Error.Validation("ankauf.reason", $"A reason (max {MaxReasonLength} characters) is required.");
        RejectionReason = reason.Trim();
        CompletedAt = now;
        Status = IntakeStatus.Rejected;
        return Result.Success();
    }

}
