using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

public enum IdDocumentType
{
    /// <summary>Personalausweis (national ID card).</summary>
    IdCard,

    /// <summary>Reisepass.</summary>
    Passport,

    /// <summary>Aufenthaltstitel (residence permit card).</summary>
    ResidencePermit,
}

/// <summary>How the seller's identity data reached the intake (stored as text).</summary>
public enum SellerCaptureMethod
{
    /// <summary>Typed in by the employee.</summary>
    Manual,

    /// <summary>Read from the machine-readable zone of the ID document (scanner, paste or camera); check digits verified.</summary>
    Mrz,

    /// <summary>Entered by the seller on their own phone (QR self check-in) and verified by the employee against the ID.</summary>
    SelfCheckIn,
}

/// <summary>
/// Who sold the device. Personal data: kept only as long as legal record-keeping requires (ADR-0009) and anonymized afterwards.
/// The optional members were added later (ADR-0016); they are omitted from the stored JSON while null, so older intakes and
/// the anonymized placeholder (checked by the database) keep their exact form.
/// </summary>
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
    /// Fixed placeholder that replaces the seller's data after the retention period (all optional members null, so they are
    /// removed too). The database checks these values (constraint <c>ck_intakes_anonymized</c>) — change both together.
    /// </summary>
    public static readonly SellerDetails Anonymized = new(AnonymizedText, AnonymizedText, AnonymizedText, "00000", AnonymizedText,
        new DateOnly(1900, 1, 1), IdDocumentType.IdCard, "ANONYMISIERT");

    /// <summary>Country of the address (ISO 3166-1 alpha-2, e.g. "DE"); null = Germany.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }

    /// <summary>Nationality as in the MRZ (ICAO code, e.g. "D" for Germany, "TUR").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Nationality { get; init; }

    /// <summary>Issuing authority (e.g. "Stadt Berlin"); printed on the card, not part of the MRZ.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdIssuingAuthority { get; init; }

    /// <summary>Issuing state as in the MRZ (ICAO code, e.g. "D").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdIssuingCountry { get; init; }

    /// <summary>Expiry date of the ID document; required for new intakes (an expired document is refused).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? IdExpiryDate { get; init; }

}

/// <summary>Rules for the seller's identity at the time of purchase (ADR-0007, ADR-0016).</summary>
public static partial class SellerRules
{
    public const int MinimumAge = 18;
    public const int MaxNameLength = 100;
    public const int MaxStreetLength = 200;
    public const int MaxPostalCodeLength = 10;
    public const int MaxCityLength = 100;
    public const int MaxDocumentNumberLength = 30;
    public const int MaxAuthorityLength = 100;

    /// <summary>Completed years between <paramref name="birth"/> and <paramref name="day"/> (29 Feb birthdays count from 1 Mar).</summary>
    public static int AgeOn(DateOnly birth, DateOnly day)
    {
        var age = day.Year - birth.Year;
        return birth > day.AddYears(-age) ? age - 1 : age;
    }

    /// <summary>The latest birth date of a seller who is already <see cref="MinimumAge"/> on <paramref name="day"/>.</summary>
    public static DateOnly LatestAdultBirthDate(DateOnly day) => day.AddYears(-MinimumAge);

    /// <summary>
    /// Checks and normalizes the seller data for a purchase on <paramref name="today"/>: required fields, a plausible birth
    /// date, minimum age 18, an ID document that has not expired, document number and ICAO codes in the expected format.
    /// </summary>
    public static Result<SellerDetails> Validate(SellerDetails seller, DateOnly today)
    {
        if (new[] { seller.FirstName, seller.LastName, seller.Street, seller.PostalCode, seller.City, seller.IdDocumentNumber }.Any(string.IsNullOrWhiteSpace))
            return Error.Validation("ankauf.seller_incomplete", "Name, address and ID document are required.");
        if (seller.BirthDate > today || seller.BirthDate.Year < 1900)
            return Error.Validation("ankauf.seller_birth_date", "The date of birth is not plausible.");
        if (AgeOn(seller.BirthDate, today) < MinimumAge)
            return Error.Validation("ankauf.seller_minor",
                    $"Devices can only be bought from sellers aged {MinimumAge} or older (born on or before {LatestAdultBirthDate(today):yyyy-MM-dd}).")
                .WithDetails(new Dictionary<string, object?> { ["minimumAge"] = MinimumAge, ["latestBirthDate"] = LatestAdultBirthDate(today).ToString("yyyy-MM-dd") });
        if (seller.IdExpiryDate is not { } expiry)
            return Error.Validation("ankauf.id_expiry_required", "The expiry date of the ID document is required.");
        if (expiry < today)
            return Error.Validation("ankauf.id_expired", $"The ID document expired on {expiry:yyyy-MM-dd}. An expired document cannot be accepted.")
                .WithDetails(new Dictionary<string, object?> { ["expiryDate"] = expiry.ToString("yyyy-MM-dd") });
        var number = Text.NormalizeKey(seller.IdDocumentNumber).Replace("<", "", StringComparison.Ordinal);
        if (!DocumentNumber().IsMatch(number))
            return Error.Validation("ankauf.id_number", "The document number may contain letters and digits only (2–30 characters).");
        string? Code(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant().TrimEnd('<');
        var nationality = Code(seller.Nationality);
        var issuing = Code(seller.IdIssuingCountry);
        if (nationality is not null && !IcaoCode().IsMatch(nationality) || issuing is not null && !IcaoCode().IsMatch(issuing))
            return Error.Validation("ankauf.country_code", "Nationality and issuing state are ICAO codes of 1–3 letters (e.g. D, TUR).");
        var country = Code(seller.Country);
        if (country is not null && !CountryCode().IsMatch(country))
            return Error.Validation("ankauf.country_code", "The address country is a two-letter ISO code (e.g. DE).");

        return seller with
        {
            FirstName = seller.FirstName.Trim(),
            LastName = seller.LastName.Trim(),
            Street = seller.Street.Trim(),
            PostalCode = seller.PostalCode.Trim(),
            City = seller.City.Trim(),
            IdDocumentNumber = number,
            Nationality = nationality,
            IdIssuingCountry = issuing,
            IdIssuingAuthority = string.IsNullOrWhiteSpace(seller.IdIssuingAuthority) ? null : seller.IdIssuingAuthority.Trim(),
            Country = country is null or "DE" ? null : country,
        };
    }

    [GeneratedRegex("^[A-Z0-9]{2,30}$")]
    private static partial Regex DocumentNumber();

    [GeneratedRegex("^[A-Z]{1,3}$")]
    private static partial Regex IcaoCode();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryCode();
}
