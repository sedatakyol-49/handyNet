using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

/// <summary>What a signature confirms (stored as text).</summary>
public enum SignaturePurpose
{
    /// <summary>
    /// Purchase: the seller confirms the declaration of ownership (Eigentumserklärung), the device as inspected and the
    /// agreed price and payment method. Required to complete an intake (unless disabled per configuration).
    /// </summary>
    Purchase,

    /// <summary>Self check-in on the seller's phone: confirms that the personal data entered is correct (no price yet).</summary>
    CheckInDeclaration,
}

/// <summary>Where the seller signed (stored as text).</summary>
public enum SignatureSource
{
    /// <summary>On the shop's screen (touch, pen or mouse).</summary>
    Counter,

    /// <summary>On the seller's own phone (QR self check-in).</summary>
    SelfCheckIn,
}

/// <summary>Limits and a minimal structural check of the signature image (PNG, as drawn on the canvas).</summary>
public static class SignatureImage
{
    public const int MaxBytes = 300_000;
    public const int MinBytes = 100;
    public const int MaxWidth = 2400;
    public const int MaxHeight = 1200;

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A PNG file (signature + IHDR chunk) of plausible size; anything else is refused.</summary>
    public static Result Validate(byte[]? png)
    {
        if (png is null || png.Length < MinBytes)
            return Error.Validation("ankauf.signature_image", "The signature is missing or empty.");
        if (png.Length > MaxBytes)
            return Error.Validation("ankauf.signature_image", $"The signature image is larger than {MaxBytes / 1000} kB.");
        if (!png.AsSpan(0, 8).SequenceEqual(PngMagic) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return Error.Validation("ankauf.signature_image", "The signature must be a PNG image.");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width is < 50 or > MaxWidth || height is < 20 or > MaxHeight)
            return Error.Validation("ankauf.signature_image", "The signature image has an implausible size.");
        return Result.Success();
    }

    public static string Sha256(byte[] png) => Convert.ToHexStringLower(SHA256.HashData(png));
}

/// <summary>
/// The data a purchase signature is bound to, as one canonical text (version 1). Its SHA-256 is stored with the signature;
/// recomputing it from the current intake and the agreed price shows whether anything changed after signing — a changed
/// seller, device, inspection, price or payment method invalidates the signature, and the seller must sign again.
/// </summary>
public static class SignaturePayload
{
    public const string Version = "HandyNet-Ankauf-Signatur-V1";

    /// <summary>Version of the declaration text shown to the seller (Eigentumserklärung + price confirmation).</summary>
    public const string DeclarationVersion = "eigentum-preis-v1";

    public static string Canonical(Intake intake, Quote quote, long paidMinor, PaymentMethod method)
    {
        var s = intake.Seller;
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.Append(Version).Append('\n');
        b.Append("declaration=").Append(DeclarationVersion).Append('\n');
        b.Append("intake=").Append(intake.Id.ToString("D")).Append('\n');
        b.Append("organization=").Append(intake.OrganizationId.ToString("D")).Append('\n');
        b.Append("seller=").AppendJoin('|', s.FirstName, s.LastName, s.BirthDate.ToString("yyyy-MM-dd", inv), s.Street, s.PostalCode, s.City,
            s.Country ?? "DE", s.Nationality ?? "", s.IdDocumentType.ToString(), s.IdDocumentNumber, s.IdExpiryDate?.ToString("yyyy-MM-dd", inv) ?? "",
            s.IdIssuingCountry ?? "", s.IdIssuingAuthority ?? "").Append('\n');
        b.Append("device=").AppendJoin('|', quote.Brand, quote.Model, quote.Storage ?? "", intake.Imei).Append('\n');
        b.Append("inspection=").AppendJoin('|', intake.InspectedCondition?.ToString() ?? "", string.Join(',', intake.InspectedDefects.Order()),
            intake.InspectedBatteryHealthPercent?.ToString(inv) ?? "", intake.InspectedOfferMinor?.ToString(inv) ?? "",
            Time(intake.InspectedAt)).Append('\n');
        b.Append("price=").Append(paidMinor.ToString(inv)).Append("|EUR|").Append(method.ToString()).Append('\n');
        return b.ToString();
    }

    public static string Hash(Intake intake, Quote quote, long paidMinor, PaymentMethod method) => Sha256(Canonical(intake, quote, paidMinor, method));

    /// <summary>Canonical text of the self check-in declaration: the personal data as submitted.</summary>
    public static string CheckInCanonical(Guid checkInId, SellerDetails s)
    {
        var inv = CultureInfo.InvariantCulture;
        return $"{Version}\ndeclaration=checkin-v1\ncheckin={checkInId:D}\nseller="
               + string.Join('|', s.FirstName, s.LastName, s.BirthDate.ToString("yyyy-MM-dd", inv), s.Street, s.PostalCode, s.City, s.Country ?? "DE",
                   s.Nationality ?? "", s.IdDocumentType.ToString(), s.IdDocumentNumber, s.IdExpiryDate?.ToString("yyyy-MM-dd", inv) ?? "") + "\n";
    }

    /// <summary>Binds image, data and time: SHA-256 of "dataHash|imageHash|signedAt".</summary>
    public static string Binding(string dataHash, string imageHash, DateTimeOffset signedAt) =>
        Sha256($"{dataHash}|{imageHash}|{Time(signedAt)}");

    /// <summary>UTC with microseconds: the precision PostgreSQL stores, so a reloaded value hashes the same.</summary>
    public static string Time(DateTimeOffset? time) =>
        time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) ?? "";

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// A signature of the private seller, drawn on the screen (or on their phone) and stored as PNG with time and hashes.
/// Append-only: a new signature is a new row; only the retention job may remove the image (personal data) later.
/// </summary>
public sealed class IntakeSignature : IAuditable
{
    private IntakeSignature() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid IntakeId { get; private set; }
    public SignaturePurpose Purpose { get; private set; }
    public SignatureSource Source { get; private set; }

    /// <summary>The PNG as drawn; null after anonymization (retention period over).</summary>
    [PersonalData]
    public byte[]? Image { get; private set; }

    public string ImageSha256 { get; private set; } = null!;

    /// <summary>SHA-256 of the canonical data the signature confirms (<see cref="SignaturePayload"/>).</summary>
    public string DataHash { get; private set; } = null!;

    /// <summary>SHA-256 over data hash, image hash and signing time.</summary>
    public string BindingHash { get; private set; } = null!;

    public string DeclarationVersion { get; private set; } = null!;
    public long? AgreedPaidMinor { get; private set; }
    public PaymentMethod? AgreedPaymentMethod { get; private set; }
    public DateTimeOffset SignedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? AnonymizedAt { get; private set; }

    /// <summary>The seller signs the purchase: allowed once the device is inspected, for an amount within the inspected offer.</summary>
    public static Result<IntakeSignature> ForPurchase(Intake intake, Quote quote, long paidMinor, PaymentMethod method, byte[] png, Guid userId,
        DateTimeOffset now)
    {
        if (intake.IsAnonymized)
            return Error.Conflict("ankauf.intake_anonymized", "The seller data of this intake was anonymized; it cannot be continued.");
        if (intake.Status != IntakeStatus.Inspected)
            return Error.Conflict("ankauf.not_inspected", "Inspect the device before the seller signs.");
        if (quote.Id != intake.QuoteId)
            throw new ArgumentException("The quote does not belong to the intake.", nameof(quote));
        if (Intake.CheckPaidAmount(paidMinor, intake.InspectedOfferMinor) is { } amountError)
            return amountError;
        if (SignatureImage.Validate(png) is { IsSuccess: false } invalid)
            return invalid.Error!;
        var dataHash = SignaturePayload.Hash(intake, quote, paidMinor, method);
        var imageHash = SignatureImage.Sha256(png);
        return new IntakeSignature
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = intake.OrganizationId,
            IntakeId = intake.Id,
            Purpose = SignaturePurpose.Purchase,
            Source = SignatureSource.Counter,
            Image = png,
            ImageSha256 = imageHash,
            DataHash = dataHash,
            BindingHash = SignaturePayload.Binding(dataHash, imageHash, now),
            DeclarationVersion = SignaturePayload.DeclarationVersion,
            AgreedPaidMinor = paidMinor,
            AgreedPaymentMethod = method,
            SignedAt = now,
            CreatedBy = userId,
        };
    }

    /// <summary>The declaration signed on the seller's phone during self check-in, attached to the intake created from it.</summary>
    public static IntakeSignature FromCheckIn(Intake intake, Guid checkInId, SellerDetails submitted, byte[] png, DateTimeOffset signedAt)
    {
        var dataHash = SignaturePayload.Sha256(SignaturePayload.CheckInCanonical(checkInId, submitted));
        var imageHash = SignatureImage.Sha256(png);
        return new IntakeSignature
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = intake.OrganizationId,
            IntakeId = intake.Id,
            Purpose = SignaturePurpose.CheckInDeclaration,
            Source = SignatureSource.SelfCheckIn,
            Image = png,
            ImageSha256 = imageHash,
            DataHash = dataHash,
            BindingHash = SignaturePayload.Binding(dataHash, imageHash, signedAt),
            DeclarationVersion = "checkin-v1",
            SignedAt = signedAt,
        };
    }

    /// <summary>
    /// Whether this purchase signature still confirms exactly the current intake and the given price/method. Also checks the
    /// stored image against its hash (unless anonymized).
    /// </summary>
    public bool Confirms(Intake intake, Quote quote, long paidMinor, PaymentMethod method) =>
        Purpose == SignaturePurpose.Purchase
        && IntakeId == intake.Id
        && DataHash == SignaturePayload.Hash(intake, quote, paidMinor, method)
        && (Image is null || SignatureImage.Sha256(Image) == ImageSha256)
        && BindingHash == SignaturePayload.Binding(DataHash, ImageSha256, SignedAt);

    /// <summary>Whether the signature still matches the intake as it is now (with the price/method it was given for).</summary>
    public bool IsCurrent(Intake intake, Quote quote) =>
        AgreedPaidMinor is { } paid && AgreedPaymentMethod is { } method && Confirms(intake, quote, paid, method);

    /// <summary>Removes the image (personal data) after the retention period; hashes and time stay as evidence. Idempotent.</summary>
    public void Anonymize(DateTimeOffset now)
    {
        if (AnonymizedAt is not null)
            return;
        Image = null;
        AnonymizedAt = now;
    }
}
