using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Imaging;

namespace HandyNet.Modules.Tenancy.Domain;

/// <summary>
/// The company data a tenant maintains itself (ADR-0014): master and contact data, tax identifiers, the texts printed on
/// till receipts and the letterhead data of its documents (bank details, managing director, commercial register) and the
/// default payment terms. Business types and status stay with the platform administration.
/// </summary>
public sealed record CompanyProfile(
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
    int? PaymentTermsDays)
{
    /// <summary>Stored form: trimmed, empty → null, VAT id/IBAN/BIC without spaces in upper case, email lower case.</summary>
    public CompanyProfile Normalized() => new(
        Name.Trim(), LegalName.Trim(), Street.Trim(), PostalCode.Trim(), City.Trim(), CountryCode.Trim().ToUpperInvariant(),
        Opt(VatId) is { } vat ? Text.NormalizeKey(vat) : null,
        Opt(TaxNumber), Opt(ContactEmail)?.ToLowerInvariant(), Opt(ContactPhone), Opt(Website),
        Multiline(ReceiptHeader), Multiline(ReceiptFooter),
        Opt(BankName), CompanyProfileRules.NormalizeIban(Iban), Opt(Bic)?.Replace(" ", "").ToUpperInvariant(),
        Opt(ManagingDirector), Opt(RegisterCourt), Opt(RegisterNumber), PaymentTermsDays);

    private static string? Opt(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Keeps line breaks (receipt texts), normalizes them to "\n" and trims trailing spaces per line.</summary>
    private static string? Multiline(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;
        var lines = v.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).Trim('\n', ' ');
    }
}

/// <summary>Result of a profile update: what was stored before and after, and whether the legal identity changed.</summary>
public sealed record CompanyProfileChange(CompanyProfile Before, CompanyProfile After, IReadOnlyList<string> LegalFieldsChanged)
{
    public bool Changed => Before != After;
}

/// <summary>Validation of the tenant-maintained company data. Same rules as the database checks where they overlap.</summary>
public static partial class CompanyProfileRules
{
    public const int MaxNameLength = 200;
    public const int MaxLegalNameLength = 300;
    public const int MaxStreetLength = 200;
    public const int MaxCityLength = 100;
    public const int MaxReceiptHeaderLength = 500;
    public const int MaxReceiptFooterLength = 1000;
    public const int MaxReceiptTextLines = 12;
    public const int MaxBankNameLength = 100;
    public const int MaxIbanLength = 34;
    public const int MaxBicLength = 11;
    public const int MaxManagingDirectorLength = 200;
    public const int MaxRegisterCourtLength = 100;
    public const int MaxRegisterNumberLength = 50;
    public const int MaxPaymentTermsDays = 365;

    /// <summary>IBAN lengths of the SEPA countries (ISO 13616 registry).</summary>
    private static readonly Dictionary<string, int> IbanLengths = new()
    {
        ["AD"] = 24, ["AT"] = 20, ["BE"] = 16, ["BG"] = 22, ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18,
        ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22, ["GI"] = 23, ["GR"] = 27, ["HR"] = 21, ["HU"] = 28,
        ["IE"] = 22, ["IS"] = 26, ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MC"] = 27, ["MT"] = 31,
        ["NL"] = 18, ["NO"] = 15, ["PL"] = 28, ["PT"] = 25, ["RO"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27,
        ["VA"] = 22, ["TR"] = 26,
    };

    public static string? NormalizeIban(string? iban) =>
        string.IsNullOrWhiteSpace(iban) ? null : new string(iban.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    /// <summary>
    /// ISO 13616 IBAN: country code, two check digits, alphanumeric account part; the country's length when known, and the
    /// mod-97 checksum must be 1. Spaces are ignored.
    /// </summary>
    public static bool IsValidIban(string? value)
    {
        var iban = NormalizeIban(value);
        if (iban is null || iban.Length is < 15 or > MaxIbanLength || !IbanShape().IsMatch(iban))
            return false;
        if (IbanLengths.TryGetValue(iban[..2], out var expected) && iban.Length != expected)
            return false;
        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(CultureInfo.InvariantCulture)));
        return BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97 == 1;
    }

    /// <summary>ISO 9362 BIC: 4 letters bank, 2 letters country, 2 alphanumeric location, optional 3 alphanumeric branch.</summary>
    public static bool IsValidBic(string? value) =>
        value is not null && BicShape().IsMatch(value.Replace(" ", "").ToUpperInvariant());

    /// <summary>
    /// VAT identification number. German ones: "DE" + nine digits. Other countries: two-letter prefix + 2–12 letters/digits
    /// (format only; no VIES lookup).
    /// </summary>
    public static bool IsValidVatId(string value, string countryCode)
    {
        var vat = Text.NormalizeKey(value);
        return countryCode.Trim().ToUpperInvariant() == "DE" ? GermanVat().IsMatch(vat) : ForeignVat().IsMatch(vat);
    }

    /// <summary>All rules over a whole profile; returns the first violation (the API validator reports them per field).</summary>
    public static Error? Validate(CompanyProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length > MaxNameLength)
            return Invalid("name", $"The display name is required (at most {MaxNameLength} characters).");
        if (string.IsNullOrWhiteSpace(p.LegalName) || p.LegalName.Trim().Length > MaxLegalNameLength)
            return Invalid("legal_name", $"The legal name is required (at most {MaxLegalNameLength} characters).");
        if (string.IsNullOrWhiteSpace(p.Street) || string.IsNullOrWhiteSpace(p.City) || string.IsNullOrWhiteSpace(p.PostalCode))
            return Invalid("address", "Street, postal code and city are required.");
        if (p.CountryCode.Trim().Length != 2)
            return Invalid("country", "The country is a two-letter ISO code.");
        if (!string.IsNullOrWhiteSpace(p.VatId) && !IsValidVatId(p.VatId, p.CountryCode))
            return Invalid("vat_id", "German VAT IDs are DE followed by nine digits.");
        if (!string.IsNullOrWhiteSpace(p.TaxNumber) && !OrganizationContactRules.IsTaxNumber(p.TaxNumber))
            return Invalid("tax_number", "A tax number (Steuernummer) has 10 to 13 digits.");
        if (!string.IsNullOrWhiteSpace(p.Website) && !OrganizationContactRules.IsWebsite(p.Website))
            return Invalid("website", "The website must start with https:// or http://.");
        if (TooLong(p.ReceiptHeader, MaxReceiptHeaderLength) || Lines(p.ReceiptHeader) > MaxReceiptTextLines)
            return Invalid("receipt_header", $"The receipt header may have at most {MaxReceiptHeaderLength} characters and {MaxReceiptTextLines} lines.");
        if (TooLong(p.ReceiptFooter, MaxReceiptFooterLength) || Lines(p.ReceiptFooter) > MaxReceiptTextLines)
            return Invalid("receipt_footer", $"The receipt footer may have at most {MaxReceiptFooterLength} characters and {MaxReceiptTextLines} lines.");
        if (!string.IsNullOrWhiteSpace(p.Iban) && !IsValidIban(p.Iban))
            return Invalid("iban", "The IBAN is not valid (check digits or length).");
        if (!string.IsNullOrWhiteSpace(p.Bic) && !IsValidBic(p.Bic))
            return Invalid("bic", "The BIC has 8 or 11 characters (e.g. COBADEFFXXX).");
        if (string.IsNullOrWhiteSpace(p.Iban) && (!string.IsNullOrWhiteSpace(p.Bic) || !string.IsNullOrWhiteSpace(p.BankName)))
            return Invalid("iban", "Enter the IBAN together with the bank name or BIC.");
        if (TooLong(p.BankName, MaxBankNameLength) || TooLong(p.ManagingDirector, MaxManagingDirectorLength)
            || TooLong(p.RegisterCourt, MaxRegisterCourtLength) || TooLong(p.RegisterNumber, MaxRegisterNumberLength))
            return Invalid("length", "A letterhead field is too long.");
        if (p.PaymentTermsDays is < 0 or > MaxPaymentTermsDays)
            return Invalid("payment_terms", $"Payment terms are 0 to {MaxPaymentTermsDays} days.");
        return null;
    }

    private static bool TooLong(string? v, int max) => v is not null && v.Trim().Length > max;

    private static int Lines(string? v) => string.IsNullOrWhiteSpace(v) ? 0 : v.Trim().Replace("\r\n", "\n").Split('\n').Length;

    private static Error Invalid(string field, string message) => Error.Validation($"company.invalid_{field}", message);

    [GeneratedRegex("^[A-Z]{2}[0-9]{2}[A-Z0-9]+$")]
    private static partial Regex IbanShape();

    [GeneratedRegex("^[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}([A-Z0-9]{3})?$")]
    private static partial Regex BicShape();

    [GeneratedRegex(@"^DE\d{9}$")]
    private static partial Regex GermanVat();

    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{2,12}$")]
    private static partial Regex ForeignVat();
}

/// <summary>
/// The company logo (ADR-0014): an upload re-encoded into a small PNG for screens and receipts and a JPEG for PDF
/// documents. One active logo per organization (partial unique index); replacing or removing keeps the row with
/// <see cref="RemovedAt"/> (no DELETE on business data). The id is random (UUIDv4) and doubles as the public token under
/// which the files are served, since the logo also appears on public e-receipts.
/// </summary>
public sealed class OrganizationLogo : IAuditable
{
    public const long MaxUploadBytes = 2 * 1024 * 1024;
    public const int ScreenMaxSide = 480;
    public const int PrintMaxSide = 1200;
    public const int MinSide = 16;
    public const int PrintJpegQuality = 90;

    public static readonly ImageDecodeLimits Limits = new(MaxUploadBytes, MinSide: MinSide);

    private OrganizationLogo() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string ScreenStorageKey { get; private set; } = null!;
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    public long ScreenSizeBytes { get; private set; }
    public string PrintStorageKey { get; private set; } = null!;
    public int PrintWidth { get; private set; }
    public int PrintHeight { get; private set; }
    public long PrintSizeBytes { get; private set; }
    public string Sha256 { get; private set; } = null!;
    public RasterFormat OriginalFormat { get; private set; }
    public long OriginalSizeBytes { get; private set; }
    public Guid UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public Guid? RemovedBy { get; private set; }

    public static string KeyFor(Guid organizationId, Guid logoId, string file) => $"organizations/{organizationId:N}/logos/{logoId:N}/{file}";

    public static OrganizationLogo Create(Guid id, Guid organizationId, EncodedImage screen, string screenKey, EncodedImage print, string printKey,
        string sha256, RasterFormat originalFormat, long originalSize, Guid uploadedBy, DateTimeOffset now) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        ScreenStorageKey = screenKey,
        ScreenWidth = screen.Width,
        ScreenHeight = screen.Height,
        ScreenSizeBytes = screen.Bytes.LongLength,
        PrintStorageKey = printKey,
        PrintWidth = print.Width,
        PrintHeight = print.Height,
        PrintSizeBytes = print.Bytes.LongLength,
        Sha256 = sha256,
        OriginalFormat = originalFormat,
        OriginalSizeBytes = originalSize,
        UploadedBy = uploadedBy,
        UploadedAt = now,
    };

    public Result Remove(Guid userId, DateTimeOffset now)
    {
        if (RemovedAt is not null)
            return Error.Conflict("company.logo_already_removed", "The logo has already been removed.");
        RemovedAt = now;
        RemovedBy = userId;
        return Result.Success();
    }
}
