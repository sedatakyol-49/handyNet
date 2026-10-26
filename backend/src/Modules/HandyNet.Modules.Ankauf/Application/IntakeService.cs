using System.Text.Json;
using FluentValidation;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Pos.Contracts;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Imaging;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Imei = HandyNet.Modules.Ankauf.Domain.Imei;

namespace HandyNet.Modules.Ankauf.Application;

/// <param name="Mrz">The machine-readable zone the seller data was taken from (optional): verified and compared with the data.</param>
/// <param name="CheckInId">A submitted QR self check-in whose data the employee verified against the ID (optional, single use).</param>
public sealed record ReceiveIntakeRequest(Guid QuoteId, string Imei, SellerDetails Seller, string? Mrz = null, Guid? CheckInId = null);

public sealed record InspectIntakeRequest(DeviceCondition Condition, DeviceDefect[]? Defects, int? BatteryHealthPercent, string? Notes);

/// <param name="RegisterId">Till the cash is taken from (required for cash purchases when the organization uses the POS module).</param>
public sealed record CompleteIntakeRequest(decimal PaidAmount, PaymentMethod PaymentMethod, Guid? WarehouseId, Guid? RegisterId = null);

/// <param name="Image">The signature as PNG, base64 (optionally as a data URL "data:image/png;base64,…").</param>
public sealed record SignIntakeRequest(decimal PaidAmount, PaymentMethod PaymentMethod, string Image);

public sealed record RejectIntakeRequest(string Reason);

public sealed record BelegFile(string FileName, byte[] Content);

/// <summary>The seller as shown to the employee; the document number is masked except its last four characters.</summary>
public sealed record SellerCardDto(
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    string Street,
    string PostalCode,
    string City,
    string? Country,
    string? Nationality,
    string IdDocumentType,
    string IdDocumentNumberMasked,
    DateOnly? IdExpiryDate,
    string? IdIssuingAuthority,
    string? IdIssuingCountry);

/// <param name="Valid">Purchase signature: still matches the intake (and its agreed amount/method). Check-in declaration: always true.</param>
public sealed record SignatureDto(
    Guid Id,
    string Purpose,
    string Source,
    DateTimeOffset SignedAt,
    long? PaidMinor,
    string? PaymentMethod,
    bool Valid,
    bool HasImage,
    string BindingHash,
    string DeclarationVersion);

public sealed record IntakeDto(
    Guid Id,
    Guid QuoteId,
    string Brand,
    string Model,
    string? Storage,
    string Imei,
    string Status,
    string SellerName,
    DateTimeOffset ReceivedAt,
    long QuotedOfferMinor,
    string? InspectedCondition,
    IReadOnlyList<string> InspectedDefects,
    int? InspectedBatteryHealthPercent,
    string? InspectionNotes,
    long? InspectedOfferMinor,
    long? InspectedMaxConsumerPriceMinor,
    long? PaidMinor,
    string? PaymentMethod,
    Guid? StockUnitId,
    DateTimeOffset? CompletedAt,
    string? RejectionReason,
    DateTimeOffset? SellerAnonymizedAt,
    SellerCardDto? Seller = null,
    string CaptureMethod = "Manual",
    SignatureDto? Signature = null,
    SignatureDto? CheckInSignature = null,
    bool SignatureRequired = false,
    Guid? CashMovementId = null,
    Guid? CashRegisterId = null,
    string? BelegNumber = null,
    DateTimeOffset? InspectedAt = null);

/// <summary>A returning seller of the organization (from earlier, not anonymized intakes). No ID document data: it is checked again.</summary>
public sealed record SellerSuggestionDto(
    Guid LatestIntakeId,
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    string Street,
    string PostalCode,
    string City,
    string? Country,
    string? Nationality,
    DateTimeOffset LastIntakeAt,
    int IntakeCount);

public sealed record PayoutRegistersDto(bool TillRequired, IReadOnlyList<PayoutRegister> Registers);

internal sealed class ReceiveIntakeValidator : AbstractValidator<ReceiveIntakeRequest>
{
    public ReceiveIntakeValidator()
    {
        RuleFor(x => x.QuoteId).NotEmpty();
        RuleFor(x => x.Imei).NotEmpty().Must(i => Imei.TryNormalize(i, out _)).WithMessage("IMEI must be 15 digits with a valid check digit.").WithErrorCode("validation.imei");
        RuleFor(x => x.Seller).NotNull().SetValidator(new SellerValidator());
        RuleFor(x => x.Mrz).MaximumLength(200);
    }
}

internal sealed class SellerValidator : AbstractValidator<SellerDetails>
{
    public SellerValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(SellerRules.MaxNameLength);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(SellerRules.MaxNameLength);
        RuleFor(x => x.Street).NotEmpty().MaximumLength(SellerRules.MaxStreetLength);
        RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(SellerRules.MaxPostalCodeLength);
        RuleFor(x => x.City).NotEmpty().MaximumLength(SellerRules.MaxCityLength);
        RuleFor(x => x.IdDocumentType).IsInEnum();
        RuleFor(x => x.IdDocumentNumber).NotEmpty().MaximumLength(SellerRules.MaxDocumentNumberLength);
        RuleFor(x => x.IdExpiryDate).NotNull().WithMessage("The expiry date of the ID document is required.").WithErrorCode("validation.idExpiryRequired");
        RuleFor(x => x.Nationality).MaximumLength(3);
        RuleFor(x => x.IdIssuingCountry).MaximumLength(3);
        RuleFor(x => x.Country).MaximumLength(2);
        RuleFor(x => x.IdIssuingAuthority).MaximumLength(SellerRules.MaxAuthorityLength);
    }
}

internal sealed class SignIntakeValidator : AbstractValidator<SignIntakeRequest>
{
    public SignIntakeValidator()
    {
        RuleFor(x => x.PaymentMethod).IsInEnum();
        RuleFor(x => x.PaidAmount).GreaterThan(0);
        RuleFor(x => x.Image).NotEmpty().MaximumLength(SignatureImage.MaxBytes * 4 / 3 + 100);
    }
}

internal static class SignatureData
{
    /// <summary>Decodes base64 (or a PNG data URL); null when it is not base64.</summary>
    public static byte[]? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        const string prefix = "data:image/png;base64,";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..];
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

internal sealed class IntakeService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IProductCatalog catalog,
    IInventory inventory,
    IOrganizationDirectory organizations,
    ICashPayouts payouts,
    IOptions<AnkaufOptions> options,
    IOrganizationBranding branding,
    IImageCodec imageCodec,
    IClock clock)
{
    /// <summary>Refusal of a device on the platform IMEI blacklist (ADR-0019); the shop sees only that it is blocked.</summary>
    internal static Error Blacklisted(string imei) =>
        Error.Conflict("imei.blacklisted", "This device is reported as lost, stolen or blocked. Do not buy it; contact the platform.")
            .WithDetails(new Dictionary<string, object?> { ["imei"] = imei });

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    private bool SignatureRequired => options.Value.RequireSellerSignature;

    public async Task<Result<IntakeDto>> ReceiveAsync(ReceiveIntakeRequest request, CancellationToken ct)
    {
        var quote = await db.Set<Quote>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == request.QuoteId && q.OrganizationId == OrganizationId, ct);
        if (quote is null)
            return Error.NotFound("ankauf.quote_not_found", "Quote not found.");
        if (await db.Set<Intake>().AnyAsync(i => i.QuoteId == quote.Id && i.Status != IntakeStatus.Rejected, ct))
            return Error.Conflict("ankauf.quote_used", "This quote is already used for an intake.");
        if (Imei.TryNormalize(request.Imei, out var imei))
        {
            // Before any money changes hands: a reported device is not bought.
            if (await inventory.IsImeiBlacklistedAsync(imei, ct))
                return Blacklisted(imei);
            if (await inventory.IsImeiInStockAnywhereAsync(imei, ct))
                return Error.Conflict("unit.imei_in_stock", "This IMEI is already in stock. Do not buy the device; check its origin.");
        }

        var capture = SellerCaptureMethod.Manual;
        if (!string.IsNullOrWhiteSpace(request.Mrz))
        {
            var mrz = Mrz.Parse(request.Mrz, Intake.Today(clock.UtcNow));
            if (!mrz.IsSuccess)
                return mrz.Error!;
            if (!Mrz.Matches(mrz.Value, request.Seller))
                return Error.Validation("ankauf.mrz_mismatch",
                    "Document number, document type, date of birth or expiry date differ from the machine-readable zone.");
            capture = SellerCaptureMethod.Mrz;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        CheckIn? checkIn = null;
        if (request.CheckInId is { } checkInId)
        {
            checkIn = await db.Set<CheckIn>().FirstOrDefaultAsync(c => c.Id == checkInId && c.OrganizationId == OrganizationId, ct);
            if (checkIn is null)
                return Error.NotFound("ankauf.checkin_not_found", "Check-in not found.");
            if (capture == SellerCaptureMethod.Manual)
                capture = SellerCaptureMethod.SelfCheckIn;
        }

        var intake = Intake.Receive(OrganizationId, quote, request.Imei, request.Seller, currentUser.UserId, clock.UtcNow, capture, checkIn?.Id);
        if (!intake.IsSuccess)
            return intake.Error!;
        db.Add(intake.Value);

        if (checkIn is not null)
        {
            var taken = checkIn.Consume(intake.Value.Id, clock.UtcNow);
            if (!taken.IsSuccess)
                return taken.Error!;
            if (taken.Value.Signature is { } png && taken.Value.SignedAt is { } signedAt)
                db.Add(IntakeSignature.FromCheckIn(intake.Value, checkIn.Id, taken.Value.Seller, png, signedAt));
        }
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("ankauf.quote_used", "This quote is already used for an intake.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("ankauf.checkin_used", "This check-in was used concurrently.");
        }
        return await ToDtoAsync(intake.Value, quote, ct);
    }

    /// <summary>Recalculates with the quote's price version and policy — the price the dealer was quoted, adjusted for the findings.</summary>
    public async Task<Result<IntakeDto>> InspectAsync(Guid id, InspectIntakeRequest request, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");

        var price = await db.Set<PriceVersion>().AsNoTracking().FirstAsync(v => v.Id == quote!.PriceVersionId, ct);
        var policy = await db.Set<ConditionPolicy>().AsNoTracking().FirstAsync(p => p.Id == quote!.ConditionPolicyId, ct);
        var findings = new QuoteInput(request.Condition, request.Defects ?? [], request.BatteryHealthPercent, null);
        var calculation = BuybackCalculator.Calculate(price.PriceMinor, policy.Settings, price.ModelKey.Split('|')[0], price.ModelKey, findings);

        var inspected = intake.Inspect(findings, calculation, request.Notes, clock.UtcNow);
        if (!inspected.IsSuccess)
            return inspected.Error!;
        return await SaveAsync(intake, quote!, ct);
    }

    /// <summary>
    /// The seller signs the purchase on the screen: declaration of ownership plus the agreed amount and payment method. The
    /// signature is bound by hash to the current intake data; any later change (re-inspection, other amount or method)
    /// invalidates it.
    /// </summary>
    public async Task<Result<IntakeDto>> SignAsync(Guid id, SignIntakeRequest request, CancellationToken ct)
    {
        if (!Money.TryFromMajor(request.PaidAmount, Money.Eur, out var paid))
            return Error.Validation("ankauf.paid_amount", "The amount may have at most two decimal places.");
        var png = SignatureData.Decode(request.Image);
        if (png is null)
            return Error.Validation("ankauf.signature_image", "The signature must be a base64-encoded PNG image.");
        var (intake, quote) = await LoadAsync(id, ct, tracking: false);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        var signature = IntakeSignature.ForPurchase(intake, quote!, paid.MinorUnits, request.PaymentMethod, png, currentUser.UserId, clock.UtcNow);
        if (!signature.IsSuccess)
            return signature.Error!;
        db.Add(signature.Value);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(intake, quote!, ct);
    }

    /// <summary>
    /// Pays the seller and stocks the device (catalog product, IMEI unit, +1 stock, movement) in one transaction; a cash payment
    /// of an organization using the POS module is taken from the chosen till in the same transaction (cash movement "Ankauf").
    /// </summary>
    public async Task<Result<IntakeDto>> CompleteAsync(Guid id, CompleteIntakeRequest request, CancellationToken ct)
    {
        if (!Money.TryFromMajor(request.PaidAmount, Money.Eur, out var paid))
            return Error.Validation("ankauf.paid_amount", "The amount may have at most two decimal places.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (intake, quote) = await LoadAsync(id, ct);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        var signature = await LatestPurchaseSignatureAsync(intake.Id, ct);
        if (intake.CheckPayable(quote!, paid.MinorUnits, request.PaymentMethod, signature, SignatureRequired) is { } notPayable)
            return notPayable;

        (Guid, Guid)? cashPayout = null;
        if (request.PaymentMethod == PaymentMethod.Cash && payouts.IsAvailable)
        {
            if (request.RegisterId is not { } registerId)
                return Error.Validation("ankauf.register_required", "Choose the register the cash is taken from.");
            var payout = await payouts.PayOutAsync(new CashPayoutRequest(registerId, CashPayoutKind.Ankauf, paid.MinorUnits, $"ankauf:{intake.Id:N}",
                $"Ankauf {quote!.Brand} {quote.Model} · IMEI {intake.Imei}"), ct);
            if (!payout.IsSuccess)
                return payout.Error!;
            cashPayout = (payout.Value.CashMovementId, payout.Value.RegisterId);
        }

        var warehouseId = request.WarehouseId ?? await inventory.GetOrStageDefaultWarehouseAsync(ct);
        var product = await catalog.FindOrStageDeviceAsync(quote!.Brand, quote.Model, quote.Storage, OrganizationId, ct);
        if (!product.IsSuccess)
            return product.Error!;

        // Bought from a private person without VAT → §25a margin scheme on resale.
        var unit = await inventory.StageReceiveUnitAsync(warehouseId, product.Value.Id, intake.Imei, paid.MinorUnits, TaxScheme.Margin25a,
            $"ankauf:{intake.Id}", ct);
        if (!unit.IsSuccess)
            return unit.Error!;

        var completed = intake.Complete(paid.MinorUnits, request.PaymentMethod, unit.Value, clock.UtcNow, quote, signature, SignatureRequired, cashPayout);
        if (!completed.IsSuccess)
            return completed.Error!;

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_cash_movements_source"))
        {
            return Error.Conflict("ankauf.already_paid", "This purchase was already paid out of a till.");
        }
        catch (DbUpdateException ex) when (ex.IsImeiBlacklisted())
        {
            return Error.Conflict("imei.blacklisted", "This device was reported as lost, stolen or blocked. The purchase was not recorded.")
                .WithDetails(new Dictionary<string, object?> { ["imei"] = intake.Imei });
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("unit.imei_in_stock", "This IMEI was stocked concurrently. The purchase was not recorded.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The intake was changed concurrently. Reload and try again.");
        }
        return await ToDtoAsync(intake, quote, ct);
    }

    public async Task<Result<IntakeDto>> RejectAsync(Guid id, string reason, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        var rejected = intake.Reject(reason, clock.UtcNow);
        if (!rejected.IsSuccess)
            return rejected.Error!;
        return await SaveAsync(intake, quote!, ct);
    }

    public async Task<IReadOnlyList<IntakeDto>> ListAsync(CancellationToken ct)
    {
        var rows = await (
                from i in db.Set<Intake>().AsNoTracking()
                join q in db.Set<Quote>().AsNoTracking() on i.QuoteId equals q.Id
                where i.OrganizationId == OrganizationId
                orderby i.ReceivedAt descending
                select new { i, q })
            .Take(100)
            .ToListAsync(ct);
        // The list shows no seller card or signatures (cheaper, and less personal data on screen).
        return rows.Select(r => ToDto(r.i, r.q, null, null, null)).ToList();
    }

    public async Task<Result<IntakeDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct, tracking: false);
        return intake is null ? Error.NotFound("ankauf.intake_not_found", "Intake not found.") : await ToDtoAsync(intake, quote!, ct);
    }

    /// <summary>The PNG of a signature of the own organization's intake (404 after anonymization).</summary>
    public async Task<Result<byte[]>> GetSignatureImageAsync(Guid intakeId, Guid signatureId, CancellationToken ct)
    {
        var image = await db.Set<IntakeSignature>().AsNoTracking()
            .Where(s => s.Id == signatureId && s.IntakeId == intakeId && s.OrganizationId == OrganizationId)
            .Select(s => s.Image).FirstOrDefaultAsync(ct);
        return image is null ? Error.NotFound("ankauf.signature_not_found", "Signature not found.") : image;
    }

    /// <summary>Purchase receipt (PDF) for the seller of a completed intake.</summary>
    public async Task<Result<BelegFile>> GetBelegAsync(Guid id, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct, tracking: false);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        if (intake.Status != IntakeStatus.Completed)
            return Error.Conflict("ankauf.not_completed", "A purchase receipt is only available for a completed intake.");

        var dealer = await organizations.GetLegalDetailsAsync(OrganizationId, ct);
        if (dealer is null)
            return Error.NotFound("organization.not_found", "Organization not found.");

        var signature = intake.SignatureId is { } sid
            ? await db.Set<IntakeSignature>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct)
            : null;
        var content = Ankaufbeleg.Build(intake, quote!,
            new BelegDealer(dealer.Name, dealer.LegalName, dealer.Street, dealer.PostalCode, dealer.City, dealer.CountryCode, dealer.VatId),
            Ankaufbeleg.GermanTimeZone,
            signature is null ? null : new BelegSignature(signature.SignedAt, signature.BindingHash, signature.Source, signature.Image));
        if (!content.IsSuccess)
            return content.Error!;
        var letterhead = (await branding.GetDocumentBrandingAsync(OrganizationId, ct)).Letterhead;
        // The signature pad delivers PNG (transparent); the PDF embeds JPEG, flattened onto white.
        byte[]? signatureJpeg = null;
        if (content.Value.SellerSignaturePng is { } png)
        {
            var jpeg = imageCodec.ToJpeg(png, new ImageDecodeLimits(SignatureImage.MaxBytes, MaxSide: SignatureImage.MaxWidth), maxSide: 800, quality: 85);
            signatureJpeg = jpeg.IsSuccess ? jpeg.Value.Bytes : null;
        }
        return new BelegFile($"{content.Value.DocumentNumber}.pdf", AnkaufbelegPdf.Render(content.Value, letterhead, signatureJpeg));
    }

    /// <summary>
    /// Earlier sellers of this organization matching the search (name), newest intake per person; anonymized intakes are
    /// excluded. ID document data is not returned: the document is presented and captured again for every purchase.
    /// </summary>
    public async Task<IReadOnlyList<SellerSuggestionDto>> SearchSellersAsync(string? search, CancellationToken ct)
    {
        var term = search?.Trim() ?? "";
        if (term.Length < 2)
            return [];
        var pattern = Text.LikePattern(term);
        var rows = await db.Database.SqlQuery<SellerRow>($"""
                SELECT id, seller_json, received_at, intake_count FROM (
                    SELECT DISTINCT ON (lower(i.seller->>'lastName'), lower(i.seller->>'firstName'), i.seller->>'birthDate')
                           i.id, i.seller::text AS seller_json, i.received_at,
                           count(*) OVER (PARTITION BY lower(i.seller->>'lastName'), lower(i.seller->>'firstName'), i.seller->>'birthDate')::int AS intake_count
                    FROM ankauf.intakes i
                    WHERE i.organization_id = {OrganizationId} AND i.anonymized_at IS NULL
                      AND (i.seller->>'lastName' ILIKE {pattern} OR i.seller->>'firstName' ILIKE {pattern}
                           OR (i.seller->>'firstName') || ' ' || (i.seller->>'lastName') ILIKE {pattern})
                    ORDER BY lower(i.seller->>'lastName'), lower(i.seller->>'firstName'), i.seller->>'birthDate', i.received_at DESC
                ) s
                ORDER BY received_at DESC
                LIMIT 20
                """).ToListAsync(ct);
        return rows.Select(r =>
        {
            var s = JsonSerializer.Deserialize<SellerDetails>(r.SellerJson, Json)!;
            return new SellerSuggestionDto(r.Id, s.FirstName, s.LastName, s.BirthDate, s.Street, s.PostalCode, s.City, s.Country, s.Nationality, r.ReceivedAt,
                r.IntakeCount);
        }).ToList();
    }

    public async Task<PayoutRegistersDto> PayoutRegistersAsync(CancellationToken ct) =>
        new(payouts.IsAvailable, await payouts.ListRegistersAsync(ct));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record SellerRow(Guid Id, string SellerJson, DateTimeOffset ReceivedAt, int IntakeCount);

    private async Task<IntakeSignature?> LatestPurchaseSignatureAsync(Guid intakeId, CancellationToken ct) =>
        await db.Set<IntakeSignature>().AsNoTracking()
            .Where(s => s.IntakeId == intakeId && s.Purpose == SignaturePurpose.Purchase)
            .OrderByDescending(s => s.SignedAt).FirstOrDefaultAsync(ct);

    private async Task<(Intake? Intake, Quote? Quote)> LoadAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var intakes = tracking ? db.Set<Intake>() : db.Set<Intake>().AsNoTracking();
        var intake = await intakes.FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (intake is null)
            return (null, null);
        var quote = await db.Set<Quote>().AsNoTracking().FirstAsync(q => q.Id == intake.QuoteId, ct);
        return (intake, quote);
    }

    private async Task<Result<IntakeDto>> SaveAsync(Intake intake, Quote quote, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The intake was changed concurrently. Reload and try again.");
        }
        return await ToDtoAsync(intake, quote, ct);
    }

    private async Task<IntakeDto> ToDtoAsync(Intake intake, Quote quote, CancellationToken ct)
    {
        var signatures = await db.Set<IntakeSignature>().AsNoTracking().Where(s => s.IntakeId == intake.Id)
            .OrderByDescending(s => s.SignedAt).ToListAsync(ct);
        // Completed: the signature used at completion; otherwise the latest purchase signature.
        var purchase = intake.SignatureId is { } used
            ? signatures.FirstOrDefault(s => s.Id == used)
            : intake.Status == IntakeStatus.Completed ? null : signatures.FirstOrDefault(s => s.Purpose == SignaturePurpose.Purchase);
        var declaration = signatures.FirstOrDefault(s => s.Purpose == SignaturePurpose.CheckInDeclaration);
        return ToDto(intake, quote, purchase, declaration, SellerCard(intake));
    }

    private static SellerCardDto? SellerCard(Intake i) => i.IsAnonymized
        ? null
        : new SellerCardDto(i.Seller.FirstName, i.Seller.LastName, i.Seller.BirthDate, i.Seller.Street, i.Seller.PostalCode, i.Seller.City,
            i.Seller.Country, i.Seller.Nationality, i.Seller.IdDocumentType.ToString(), Ankaufbeleg.MaskIdNumber(i.Seller.IdDocumentNumber),
            i.Seller.IdExpiryDate, i.Seller.IdIssuingAuthority, i.Seller.IdIssuingCountry);

    private IntakeDto ToDto(Intake i, Quote q, IntakeSignature? purchase, IntakeSignature? declaration, SellerCardDto? seller) => new(
        i.Id, i.QuoteId, q.Brand, q.Model, q.Storage, i.Imei, i.Status.ToString(),
        i.IsAnonymized ? SellerDetails.AnonymizedText : $"{i.Seller.FirstName} {i.Seller.LastName}", i.ReceivedAt,
        q.PlatformOfferMinor, i.InspectedCondition?.ToString(), i.InspectedDefects.Select(d => d.ToString()).ToList(), i.InspectedBatteryHealthPercent,
        i.InspectionNotes, i.InspectedOfferMinor, i.InspectedMaxConsumerPriceMinor, i.PaidMinor, i.PaymentMethod?.ToString(), i.StockUnitId,
        i.CompletedAt, i.RejectionReason, i.AnonymizedAt,
        seller,
        i.CaptureMethod.ToString(),
        purchase is null ? null : SignatureDtoOf(purchase, i.Status == IntakeStatus.Completed ? i.SignatureId == purchase.Id : purchase.IsCurrent(i, q)),
        declaration is null ? null : SignatureDtoOf(declaration, true),
        SignatureRequired,
        i.CashMovementId,
        i.CashRegisterId,
        i is { Status: IntakeStatus.Completed, CompletedAt: { } at }
            ? Ankaufbeleg.DocumentNumber(i.Id, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Ankaufbeleg.GermanTimeZone).DateTime))
            : null,
        i.InspectedAt);

    private static SignatureDto SignatureDtoOf(IntakeSignature s, bool valid) => new(s.Id, s.Purpose.ToString(), s.Source.ToString(), s.SignedAt,
        s.AgreedPaidMinor, s.AgreedPaymentMethod?.ToString(), valid, s.Image is not null, s.BindingHash, s.DeclarationVersion);
}
