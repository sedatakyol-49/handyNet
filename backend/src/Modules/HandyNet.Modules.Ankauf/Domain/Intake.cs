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

public enum IdDocumentType
{
    IdCard,
    Passport,
    ResidencePermit,
}

public enum PaymentMethod
{
    Cash,
    BankTransfer,
}

/// <summary>Who sold the device. Personal data: kept only as long as legal record-keeping requires (ADR-0009).</summary>
public sealed record SellerDetails(
    string FirstName,
    string LastName,
    string Street,
    string PostalCode,
    string City,
    DateOnly BirthDate,
    IdDocumentType IdDocumentType,
    string IdDocumentNumber)
{
    public const string AnonymizedText = "anonymisiert";

    /// <summary>
    /// Fixed placeholder that replaces the seller's data after the retention period. The database checks these values
    /// (constraint <c>ck_intakes_anonymized</c>) — change both together.
    /// </summary>
    public static readonly SellerDetails Anonymized = new(AnonymizedText, AnonymizedText, AnonymizedText, "00000", AnonymizedText,
        new DateOnly(1900, 1, 1), IdDocumentType.IdCard, "ANONYMISIERT");
}

/// <summary>
/// The physical purchase of a used device from a private seller, based on a preliminary quote. The final price is
/// recalculated after inspection with the quote's own price version and policy (never today's prices), and the
/// dealer may pay at most the inspected platform offer. Completing it records the payment and stocks the IMEI.
/// </summary>
public sealed class Intake : IAuditable
{
    public const int MinimumSellerAge = 18;
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
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? RejectionReason { get; private set; }
    public uint Version { get; private set; }

    public static Result<Intake> Receive(Guid organizationId, Quote quote, string imei, SellerDetails seller, Guid userId, DateTimeOffset now)
    {
        if (quote.OrganizationId != organizationId)
            return Error.NotFound("ankauf.quote_not_found", "Quote not found.");
        if (quote.ValidUntil <= now)
            return Error.Conflict("ankauf.quote_expired", "The quote has expired. Create a new quote with current prices.");
        if (!Domain.Imei.TryNormalize(imei, out var normalized))
            return Error.Validation("ankauf.imei", "IMEI must be 15 digits with a valid check digit.");
        if (quote.Imei is { } quoted && quoted != normalized)
            return Error.Validation("ankauf.imei_mismatch", "The IMEI differs from the one in the quote.");
        if (AgeOn(seller.BirthDate, DateOnly.FromDateTime(now.UtcDateTime)) < MinimumSellerAge)
            return Error.Validation("ankauf.seller_minor", $"Devices can only be bought from sellers aged {MinimumSellerAge} or older.");
        if (new[] { seller.FirstName, seller.LastName, seller.Street, seller.PostalCode, seller.City, seller.IdDocumentNumber }.Any(string.IsNullOrWhiteSpace))
            return Error.Validation("ankauf.seller_incomplete", "Name, address and ID document are required.");

        return new Intake
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            QuoteId = quote.Id,
            Imei = normalized,
            Seller = seller with
            {
                FirstName = seller.FirstName.Trim(),
                LastName = seller.LastName.Trim(),
                IdDocumentNumber = Text.NormalizeKey(seller.IdDocumentNumber),
            },
            Status = IntakeStatus.Received,
            ReceivedAt = now,
            ReceivedBy = userId,
        };
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

    public Result Complete(long paidMinor, PaymentMethod method, Guid stockUnitId, DateTimeOffset now)
    {
        if (IsAnonymized)
            return Error.Conflict("ankauf.intake_anonymized", "The seller data of this intake was anonymized; it cannot be continued.");
        if (Status != IntakeStatus.Inspected)
            return Error.Conflict("ankauf.not_inspected", "Inspect the device before paying the seller.");
        if (paidMinor <= 0)
            return Error.Validation("ankauf.paid_amount", "The amount paid must be positive.");
        if (paidMinor > InspectedOfferMinor)
            return Error.Validation("ankauf.paid_above_offer",
                $"The amount exceeds the inspected offer ({InspectedOfferMinor / 100m:0.00} €) and would be sold at a loss.");
        PaidMinor = paidMinor;
        PaymentMethod = method;
        StockUnitId = stockUnitId;
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

    private static int AgeOn(DateOnly birth, DateOnly day)
    {
        var age = day.Year - birth.Year;
        return birth > day.AddYears(-age) ? age - 1 : age;
    }
}
