using System.Text.Json;
using FluentValidation;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Ankauf.Application;

/// <param name="Path">Relative link of the public form; the QR code encodes the absolute URL.</param>
public sealed record CheckInCreatedDto(Guid Id, string Token, string Path, DateTimeOffset ExpiresAt);

/// <summary>For the employee: status and — once submitted — the data to verify against the ID document.</summary>
public sealed record CheckInDto(Guid Id, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? SubmittedAt,
    SellerDetails? Seller, bool HasSignature, DateTimeOffset? SignedAt, Guid? IntakeId);

/// <summary>What the public form may know: the shop's name and whether the link is still usable. Nothing else.</summary>
public sealed record PublicCheckInDto(string ShopName, DateTimeOffset ExpiresAt, string Status, int MinimumAge, string Declaration);

/// <param name="Signature">Optional PNG (base64 or data URL) confirming that the data is correct.</param>
/// <param name="Consent">The seller agrees that the shop processes the data for the purchase (privacy notice shown on the form).</param>
public sealed record PublicCheckInRequest(SellerDetails Seller, string? Signature, bool Consent);

internal sealed class PublicCheckInValidator : AbstractValidator<PublicCheckInRequest>
{
    public PublicCheckInValidator()
    {
        RuleFor(x => x.Seller).NotNull().SetValidator(new SellerValidator());
        RuleFor(x => x.Consent).Equal(true).WithMessage("Please confirm the privacy notice.");
        RuleFor(x => x.Signature).MaximumLength(SignatureImage.MaxBytes * 4 / 3 + 100);
    }
}

internal sealed class CheckInService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IOrganizationDirectory organizations,
    IOptions<AnkaufOptions> options,
    IClock clock)
{
    public const string PublicPathPrefix = "/checkin/";

    public const string CheckInDeclaration =
        "Ich bestätige, dass meine Angaben richtig und vollständig sind und mit meinem Ausweisdokument übereinstimmen.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<CheckInCreatedDto>> CreateAsync(CancellationToken ct)
    {
        var org = tenant.RequiredOrganizationId;
        var details = await organizations.GetLegalDetailsAsync(org, ct);
        if (details is null)
            return Error.NotFound("organization.not_found", "Organization not found.");
        var (checkIn, token) = CheckIn.Create(org, details.Name, currentUser.UserId, clock.UtcNow, options.Value.CheckInMinutes);
        db.Add(checkIn);
        await db.SaveChangesAsync(ct);
        return new CheckInCreatedDto(checkIn.Id, token, PublicPathPrefix + token, checkIn.ExpiresAt);
    }

    public async Task<Result<CheckInDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<CheckIn>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == tenant.RequiredOrganizationId, ct);
        if (c is null)
            return Error.NotFound("ankauf.checkin_not_found", "Check-in not found.");
        var status = c.StatusAt(clock.UtcNow);
        return new CheckInDto(c.Id, status.ToString(), c.CreatedAt, c.ExpiresAt, c.SubmittedAt, status == CheckInStatus.Submitted ? c.Seller : null,
            c.SignatureImage is not null, c.SignedAt, c.IntakeId);
    }

    public async Task<Result<byte[]>> GetSignatureAsync(Guid id, CancellationToken ct)
    {
        var image = await db.Set<CheckIn>().AsNoTracking().Where(x => x.Id == id && x.OrganizationId == tenant.RequiredOrganizationId)
            .Select(x => x.SignatureImage).FirstOrDefaultAsync(ct);
        return image is null ? Error.NotFound("ankauf.signature_not_found", "Signature not found.") : image;
    }

    // ---- public (anonymous) side: only through SECURITY DEFINER functions, by the SHA-256 of the token -----------------------

    private static Error PublicNotFound => Error.NotFound("ankauf.checkin_link_invalid", "This check-in link does not exist or has expired.");

    public async Task<Result<PublicCheckInDto>> GetPublicAsync(string token, CancellationToken ct)
    {
        if (!CheckInToken.LooksValid(token))
            return PublicNotFound;
        var rows = await db.Database.SqlQuery<PublicRow>(
            $"SELECT shop_name, expires_at, submitted FROM ankauf.checkin_public({CheckInToken.HashOf(token)})").ToListAsync(ct);
        if (rows.FirstOrDefault() is not { } row)
            return PublicNotFound;
        var status = row.Submitted ? CheckInStatus.Submitted : row.ExpiresAt > clock.UtcNow ? CheckInStatus.Open : CheckInStatus.Expired;
        if (status == CheckInStatus.Expired)
            return PublicNotFound;
        return new PublicCheckInDto(row.ShopName, row.ExpiresAt, status.ToString(), SellerRules.MinimumAge, CheckInDeclaration);
    }

    /// <summary>
    /// The seller submits once: the data is checked like at the counter (age 18+, valid ID document), the optional signature
    /// must be a PNG. The write happens only if the link is unexpired and was never submitted (in the database function).
    /// </summary>
    public async Task<Result> SubmitPublicAsync(string token, PublicCheckInRequest request, CancellationToken ct)
    {
        if (!CheckInToken.LooksValid(token))
            return PublicNotFound;
        var seller = SellerRules.Validate(request.Seller, Intake.Today(clock.UtcNow));
        if (!seller.IsSuccess)
            return seller.Error!;
        byte[]? png = null;
        if (!string.IsNullOrWhiteSpace(request.Signature))
        {
            png = SignatureData.Decode(request.Signature);
            if (SignatureImage.Validate(png) is { IsSuccess: false } invalid)
                return invalid.Error!;
        }
        var sellerJson = JsonSerializer.Serialize(seller.Value, Json);
        var signedAt = png is null ? (DateTimeOffset?)null : clock.UtcNow;
        var written = await db.Database.SqlQuery<int>(
                $"SELECT ankauf.checkin_submit({CheckInToken.HashOf(token)}, {sellerJson}::jsonb, {png}::bytea, {signedAt}::timestamptz) AS \"Value\"")
            .ToListAsync(ct);
        return written.FirstOrDefault() switch
        {
            1 => Result.Success(),
            2 => Error.Conflict("ankauf.checkin_already_submitted", "This check-in was already submitted. Please speak to the staff."),
            _ => PublicNotFound,
        };
    }

    private sealed record PublicRow(string ShopName, DateTimeOffset ExpiresAt, bool Submitted);
}
