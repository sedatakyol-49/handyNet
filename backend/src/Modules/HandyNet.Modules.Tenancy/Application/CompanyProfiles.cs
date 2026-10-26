using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Documents;
using HandyNet.SharedKernel.Imaging;
using HandyNet.SharedKernel.Storage;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Tenancy.Application;

/// <param name="ScreenUrl">Small PNG (≤ 480 px) for screens and till receipts; public, immutable.</param>
/// <param name="PrintUrl">JPEG (≤ 1200 px, white background) as embedded in PDF documents; public, immutable.</param>
public sealed record CompanyLogoDto(Guid Id, string ScreenUrl, string PrintUrl, int Width, int Height, DateTimeOffset UploadedAt);

/// <param name="Version">Concurrency token; send it back with <see cref="UpdateCompanyProfileRequest"/>.</param>
/// <param name="CanEdit">Whether the caller holds <c>organization.manage</c> (owners).</param>
public sealed record CompanyProfileDto(
    Guid Id,
    string Name,
    string LegalName,
    string Street,
    string PostalCode,
    string City,
    string CountryCode,
    string? VatId,
    string? TaxNumber,
    string? ContactEmail,
    string? ContactPhone,
    string? Website,
    string? ReceiptHeader,
    string? ReceiptFooter,
    string? BankName,
    string? Iban,
    string? Bic,
    string? ManagingDirector,
    string? RegisterCourt,
    string? RegisterNumber,
    int? PaymentTermsDays,
    CompanyLogoDto? Logo,
    string Status,
    uint Version,
    bool CanEdit,
    string EmailLanguage = CompanyProfileRules.DefaultEmailLanguage);

/// <param name="Version">The <see cref="CompanyProfileDto.Version"/> the form was loaded with (required: optimistic concurrency).</param>
public sealed record UpdateCompanyProfileRequest(
    string Name,
    string LegalName,
    string Street,
    string PostalCode,
    string City,
    string? CountryCode,
    string? VatId,
    string? TaxNumber,
    string? ContactEmail,
    string? ContactPhone,
    string? Website,
    string? ReceiptHeader,
    string? ReceiptFooter,
    string? BankName,
    string? Iban,
    string? Bic,
    string? ManagingDirector,
    string? RegisterCourt,
    string? RegisterNumber,
    int? PaymentTermsDays,
    uint? Version,
    string? EmailLanguage = null)
{
    /// <param name="currentEmailLanguage">Kept when the request carries no language (older clients).</param>
    public CompanyProfile ToProfile(string? currentEmailLanguage = null) => new(Name ?? "", LegalName ?? "", Street ?? "", PostalCode ?? "", City ?? "",
        string.IsNullOrWhiteSpace(CountryCode) ? "DE" : CountryCode, VatId, TaxNumber, ContactEmail, ContactPhone, Website, ReceiptHeader,
        ReceiptFooter, BankName, Iban, Bic, ManagingDirector, RegisterCourt, RegisterNumber, PaymentTermsDays,
        EmailLanguage ?? currentEmailLanguage ?? CompanyProfileRules.DefaultEmailLanguage);
}

internal sealed class UpdateCompanyProfileValidator : AbstractValidator<UpdateCompanyProfileRequest>
{
    public UpdateCompanyProfileValidator()
    {
        RuleFor(x => x.Version).NotNull().WithMessage("Send the version the form was loaded with.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CompanyProfileRules.MaxNameLength);
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(CompanyProfileRules.MaxLegalNameLength);
        RuleFor(x => x.Street).NotEmpty().MaximumLength(CompanyProfileRules.MaxStreetLength);
        RuleFor(x => x.City).NotEmpty().MaximumLength(CompanyProfileRules.MaxCityLength);
        RuleFor(x => x.CountryCode).Matches("^[A-Za-z]{2}$").When(x => !string.IsNullOrWhiteSpace(x.CountryCode))
            .WithMessage("The country is a two-letter ISO code.");
        RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(10);
        RuleFor(x => x.PostalCode).Matches(@"^\d{5}$").When(x => IsGerman(x) && !string.IsNullOrWhiteSpace(x.PostalCode))
            .WithMessage("German postal codes have five digits.");
        RuleFor(x => x.VatId!).Must((x, vat) => CompanyProfileRules.IsValidVatId(vat, x.CountryCode ?? "DE")).MaximumLength(20)
            .When(x => !string.IsNullOrWhiteSpace(x.VatId))
            .WithMessage(x => IsGerman(x) ? "German VAT IDs are DE followed by nine digits." : "Enter the VAT ID with its two-letter country prefix.");
        RuleFor(x => x.TaxNumber!).Must(OrganizationContactRules.IsTaxNumber).When(x => !string.IsNullOrWhiteSpace(x.TaxNumber))
            .WithMessage("A tax number (Steuernummer) has 10 to 13 digits, optionally separated by / or spaces.");
        RuleFor(x => x.ContactEmail!).EmailAddress().MaximumLength(Organization.MaxContactEmailLength).When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x.ContactPhone!).Matches(@"^\+?[0-9 ()/\-]{6,40}$").When(x => !string.IsNullOrWhiteSpace(x.ContactPhone))
            .WithMessage("Enter a phone number with digits, spaces, ( ) / - and an optional leading +.");
        RuleFor(x => x.Website!).Must(OrganizationContactRules.IsWebsite).MaximumLength(Organization.MaxWebsiteLength)
            .When(x => !string.IsNullOrWhiteSpace(x.Website))
            .WithMessage("The website must be a full address starting with https:// or http://.");
        RuleFor(x => x.ReceiptHeader).MaximumLength(CompanyProfileRules.MaxReceiptHeaderLength)
            .Must(t => LineCount(t) <= CompanyProfileRules.MaxReceiptTextLines).WithMessage($"At most {CompanyProfileRules.MaxReceiptTextLines} lines.");
        RuleFor(x => x.ReceiptFooter).MaximumLength(CompanyProfileRules.MaxReceiptFooterLength)
            .Must(t => LineCount(t) <= CompanyProfileRules.MaxReceiptTextLines).WithMessage($"At most {CompanyProfileRules.MaxReceiptTextLines} lines.");
        RuleFor(x => x.Iban!).Must(CompanyProfileRules.IsValidIban).When(x => !string.IsNullOrWhiteSpace(x.Iban))
            .WithMessage("The IBAN is not valid (check digits or length).");
        RuleFor(x => x.Iban).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.Bic) || !string.IsNullOrWhiteSpace(x.BankName))
            .WithMessage("Enter the IBAN together with the bank name or BIC.");
        RuleFor(x => x.Bic!).Must(CompanyProfileRules.IsValidBic).When(x => !string.IsNullOrWhiteSpace(x.Bic))
            .WithMessage("The BIC has 8 or 11 characters (e.g. COBADEFFXXX).");
        RuleFor(x => x.BankName).MaximumLength(CompanyProfileRules.MaxBankNameLength);
        RuleFor(x => x.ManagingDirector).MaximumLength(CompanyProfileRules.MaxManagingDirectorLength);
        RuleFor(x => x.RegisterCourt).MaximumLength(CompanyProfileRules.MaxRegisterCourtLength);
        RuleFor(x => x.RegisterNumber).MaximumLength(CompanyProfileRules.MaxRegisterNumberLength);
        RuleFor(x => x.EmailLanguage!).Must(l => CompanyProfileRules.IsEmailLanguage(l.Trim().ToLowerInvariant())).When(x => x.EmailLanguage is not null)
            .WithMessage("The email language is one of: de, en, tr, es, ru, ar, fa.");
        RuleFor(x => x.PaymentTermsDays).InclusiveBetween(0, CompanyProfileRules.MaxPaymentTermsDays).When(x => x.PaymentTermsDays is not null);
    }

    private static bool IsGerman(UpdateCompanyProfileRequest x) => string.IsNullOrWhiteSpace(x.CountryCode) || x.CountryCode.Trim().Equals("DE", StringComparison.OrdinalIgnoreCase);

    private static int LineCount(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Replace("\r\n", "\n").Split('\n').Length;
}

internal static class LogoUrls
{
    public static string Screen(Guid id) => $"/api/logos/{id}/screen";
    public static string Print(Guid id) => $"/api/logos/{id}/print";
}

public enum LogoVariant
{
    Screen,
    Print,
}

internal sealed record LogoContent(StoredFile File, string ETag);

/// <summary>
/// The tenant's own company profile and logo (ADR-0014). Every call is scoped to the tenant of the request
/// (<see cref="ITenantContext.RequiredOrganizationId"/>) — there is no organization id in the routes; the database adds an
/// UPDATE policy on <c>tenancy.organizations</c> and RLS on the logos so another organization's row cannot be changed even
/// by a faulty query.
/// </summary>
internal sealed class CompanyProfileService(
    HandyNetDbContext db,
    IFileStorage storage,
    IImageCodec codec,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<CompanyProfileService> logger)
{
    public const string AuditSource = "tenant";
    public const string UpdateAction = "TenantProfileUpdate";

    private static readonly JsonSerializerOptions AuditJson = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly Error NotFound = Error.NotFound("organization.not_found", "Organization not found.");

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<CompanyProfileDto>> GetAsync(CancellationToken ct)
    {
        var organization = await db.Set<Organization>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == OrganizationId, ct);
        if (organization is null)
            return NotFound;
        return ToDto(organization, await ActiveLogoAsync(organization.Id, tracked: false, ct), db.Entry(organization).Property(o => o.Version).CurrentValue);
    }

    public async Task<Result<CompanyProfileDto>> UpdateAsync(UpdateCompanyProfileRequest request, CancellationToken ct)
    {
        var organization = await db.Set<Organization>().FirstOrDefaultAsync(o => o.Id == OrganizationId, ct);
        if (organization is null)
            return NotFound;
        if (request.Version is { } expected)
            db.Entry(organization).Property(o => o.Version).OriginalValue = expected;

        var change = organization.UpdateCompanyProfile(request.ToProfile(organization.EmailLanguage), currentUser.UserId, clock.UtcNow);
        if (!change.IsSuccess)
            return change.Error!;
        if (change.Value.Changed)
        {
            if (organization.VatId is { } vat && await db.Set<Organization>().AnyAsync(o => o.VatId == vat && o.Id != organization.Id, ct))
                return Error.Conflict("organization.vat_taken", "An organization with this VAT ID is already registered.");

            db.Set<AuditEntry>().Add(new AuditEntry
            {
                OccurredAt = clock.UtcNow,
                UserId = currentUser.UserId,
                OrganizationId = organization.Id,
                EntityType = nameof(Organization),
                EntityId = organization.Id.ToString(),
                Action = UpdateAction,
                Changes = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["Source"] = AuditSource,
                    ["Before"] = change.Value.Before,
                    ["After"] = change.Value.After,
                    ["LegalFieldsChanged"] = change.Value.LegalFieldsChanged,
                }, AuditJson),
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Error.Conflict("concurrency", "The company data was changed by someone else. Reload and try again.");
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return Error.Conflict("organization.vat_taken", "An organization with this VAT ID is already registered.");
            }
        }
        return ToDto(organization, await ActiveLogoAsync(organization.Id, tracked: false, ct), db.Entry(organization).Property(o => o.Version).CurrentValue);
    }

    /// <summary>
    /// Replaces the logo: validated by magic bytes and size, re-encoded (PNG for screens, JPEG for PDFs), files written
    /// first, then the row committed (previous logo marked removed in the same transaction); files of a failed commit and of
    /// the replaced logo are deleted afterwards.
    /// </summary>
    public async Task<Result<CompanyLogoDto>> UploadLogoAsync(byte[] data, CancellationToken ct)
    {
        if (OrganizationLogo.Limits.CheckSize(data.LongLength) is { } sizeError)
            return sizeError;
        var rendered = codec.Render(data, OrganizationLogo.Limits,
        [
            new RenditionSpec(OrganizationLogo.ScreenMaxSide, RasterFormat.Png),
            new RenditionSpec(OrganizationLogo.PrintMaxSide, RasterFormat.Jpeg, OrganizationLogo.PrintJpegQuality, FlattenOnWhite: true),
        ]);
        if (!rendered.IsSuccess)
            return rendered.Error!;
        var screen = rendered.Value.Images[0];
        var print = rendered.Value.Images[1];

        var organizationId = OrganizationId;
        var logoId = Guid.NewGuid(); // random: the id is the public token of the logo files
        var screenKey = OrganizationLogo.KeyFor(organizationId, logoId, "screen.png");
        var printKey = OrganizationLogo.KeyFor(organizationId, logoId, "print.jpg");
        await PutAsync(screenKey, screen);
        await PutAsync(printKey, print);

        OrganizationLogo? replaced;
        OrganizationLogo logo;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockOrganizationAsync(organizationId, ct);
            replaced = await ActiveLogoAsync(organizationId, tracked: true, ct);
            replaced?.Remove(currentUser.UserId, clock.UtcNow);
            if (replaced is not null)
                await db.SaveChangesAsync(ct); // the partial unique index allows the new active row only after this
            logo = OrganizationLogo.Create(logoId, organizationId, screen, screenKey, print, printKey,
                Convert.ToHexStringLower(SHA256.HashData(print.Bytes)), rendered.Value.OriginalFormat, data.LongLength, currentUser.UserId, clock.UtcNow);
            db.Add(logo);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException)
        {
            logger.LogWarning(ex, "Storing logo {LogoId} of organization {OrganizationId} failed; removing its files", logoId, organizationId);
            await DeleteFilesAsync(screenKey, printKey);
            return Error.Conflict("company.logo_concurrent", "The logo was changed concurrently. Try again.");
        }

        if (replaced is not null)
            await DeleteFilesAsync(replaced.ScreenStorageKey, replaced.PrintStorageKey);
        return ToLogoDto(logo);
    }

    public async Task<Result> RemoveLogoAsync(CancellationToken ct)
    {
        OrganizationLogo? active;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockOrganizationAsync(OrganizationId, ct);
            active = await ActiveLogoAsync(OrganizationId, tracked: true, ct);
            if (active is null)
                return Error.NotFound("company.logo_not_found", "The company has no logo.");
            var removed = active.Remove(currentUser.UserId, clock.UtcNow);
            if (!removed.IsSuccess)
                return removed;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        await DeleteFilesAsync(active.ScreenStorageKey, active.PrintStorageKey);
        return Result.Success();
    }

    /// <summary>Anonymous read of an active logo by its random id (RLS: logos are readable by everyone).</summary>
    public async Task<LogoContent?> OpenLogoAsync(Guid logoId, LogoVariant variant, CancellationToken ct)
    {
        var logo = await db.Set<OrganizationLogo>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == logoId && l.RemovedAt == null, ct);
        if (logo is null)
            return null;
        var file = await storage.OpenReadAsync(variant == LogoVariant.Print ? logo.PrintStorageKey : logo.ScreenStorageKey, ct);
        if (file is null)
        {
            logger.LogError("Logo {LogoId} has no stored {Variant} file", logoId, variant);
            return null;
        }
        return new LogoContent(file, $"\"{logo.Id:N}-{(variant == LogoVariant.Print ? "p" : "s")}\"");
    }

    internal static CompanyLogoDto ToLogoDto(OrganizationLogo l) =>
        new(l.Id, LogoUrls.Screen(l.Id), LogoUrls.Print(l.Id), l.ScreenWidth, l.ScreenHeight, l.UploadedAt);

    private CompanyProfileDto ToDto(Organization o, OrganizationLogo? logo, uint version) => new(
        o.Id, o.Name, o.LegalName, o.Street, o.PostalCode, o.City, o.CountryCode, o.VatId, o.TaxNumber, o.ContactEmail, o.ContactPhone,
        o.Website, o.ReceiptHeader, o.ReceiptFooter, o.BankName, o.Iban, o.Bic, o.ManagingDirector, o.RegisterCourt, o.RegisterNumber,
        o.PaymentTermsDays, logo is null ? null : ToLogoDto(logo), o.Status.ToString(), version,
        tenant.Permissions.Contains(Permissions.OrganizationManage), o.EmailLanguage);

    private Task<OrganizationLogo?> ActiveLogoAsync(Guid organizationId, bool tracked, CancellationToken ct)
    {
        var query = db.Set<OrganizationLogo>().Where(l => l.OrganizationId == organizationId && l.RemovedAt == null);
        return (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    /// <summary>Serializes logo changes of one organization (the row lock does not change its concurrency token).</summary>
    private Task LockOrganizationAsync(Guid organizationId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenancy.organizations WHERE id = {organizationId} FOR UPDATE", ct);

    private async Task PutAsync(string key, EncodedImage image)
    {
        using var stream = new MemoryStream(image.Bytes, writable: false);
        await storage.PutAsync(key, stream, image.ContentType, CancellationToken.None);
    }

    private async Task DeleteFilesAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete stored file {Key}", key);
            }
        }
    }
}

/// <summary>
/// <see cref="IOrganizationBranding"/>: receipt texts and logo for receipts (also anonymous e-receipts), letterhead for
/// PDF documents. Reads only the organization row and its active logo.
/// </summary>
internal sealed class OrganizationBrandingSource(HandyNetDbContext db, IFileStorage storage, ILogger<OrganizationBrandingSource> logger)
    : IOrganizationBranding
{
    public async Task<ReceiptBranding> GetReceiptBrandingAsync(Guid organizationId, CancellationToken ct)
    {
        var o = await db.Set<Organization>().AsNoTracking().Where(x => x.Id == organizationId)
            .Select(x => new { x.ReceiptHeader, x.ReceiptFooter }).FirstOrDefaultAsync(ct);
        if (o is null)
            return ReceiptBranding.None;
        var logoId = await db.Set<OrganizationLogo>().AsNoTracking().Where(l => l.OrganizationId == organizationId && l.RemovedAt == null)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
        return new ReceiptBranding(logoId is { } id ? LogoUrls.Screen(id) : null, o.ReceiptHeader, o.ReceiptFooter);
    }

    public async Task<DocumentBranding> GetDocumentBrandingAsync(Guid organizationId, CancellationToken ct)
    {
        var o = await db.Set<Organization>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == organizationId, ct);
        if (o is null)
            return new DocumentBranding(DocumentLetterhead.None, null);
        var logo = await db.Set<OrganizationLogo>().AsNoTracking().FirstOrDefaultAsync(l => l.OrganizationId == organizationId && l.RemovedAt == null, ct);
        byte[]? jpeg = null;
        if (logo is not null)
        {
            try
            {
                await using var file = await storage.OpenReadAsync(logo.PrintStorageKey, ct);
                if (file is null)
                {
                    logger.LogError("Logo {LogoId} has no stored print file; documents are rendered without it", logo.Id);
                }
                else
                {
                    using var buffer = new MemoryStream();
                    await file.Content.CopyToAsync(buffer, ct);
                    jpeg = buffer.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                logger.LogError(ex, "Reading logo {LogoId} failed; documents are rendered without it", logo.Id);
            }
        }
        return new DocumentBranding(new DocumentLetterhead(jpeg, FooterLines(o.CompanyProfile)), o.PaymentTermsDays);
    }

    /// <summary>German letterhead footer: issuer and contact, management/register/tax ids, bank details.</summary>
    internal static IReadOnlyList<string> FooterLines(CompanyProfile p)
    {
        static string Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));

        var lines = new List<string>
        {
            Join([p.LegalName, $"{p.Street}, {p.PostalCode} {p.City}", p.ContactPhone is null ? null : $"Tel. {p.ContactPhone}", p.ContactEmail, p.Website]),
        };
        var register = Join([p.RegisterCourt, p.RegisterNumber]).Replace(" · ", " ");
        var legal = Join([
            p.ManagingDirector is null ? null : $"Geschäftsführung: {p.ManagingDirector}",
            register.Length == 0 ? null : $"Registergericht: {register}",
            p.VatId is null ? null : $"USt-IdNr.: {p.VatId}",
            p.TaxNumber is null ? null : $"Steuernummer: {p.TaxNumber}",
        ]);
        if (legal.Length > 0)
            lines.Add(legal);
        if (p.Iban is not null)
            lines.Add("Bankverbindung: " + Join([p.BankName, $"IBAN {FormatIban(p.Iban)}", p.Bic is null ? null : $"BIC {p.Bic}"]));
        return lines;
    }

    /// <summary>"DE89370400440532013000" → "DE89 3704 0044 0532 0130 00".</summary>
    internal static string FormatIban(string iban) =>
        string.Join(' ', Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));
}
