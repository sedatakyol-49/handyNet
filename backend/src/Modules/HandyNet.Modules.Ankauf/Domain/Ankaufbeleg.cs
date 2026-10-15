using System.Globalization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

/// <summary>The dealer (buyer) as printed on the purchase receipt.</summary>
public sealed record BelegDealer(string Name, string LegalName, string Street, string PostalCode, string City, string CountryCode, string? VatId);

public sealed record BelegField(string Label, string Value);

public sealed record BelegSection(string Heading, IReadOnlyList<BelegField> Fields, IReadOnlyList<string> Paragraphs);

/// <summary>Everything printed on an Ankaufbeleg, already formatted (German). Rendering only lays it out.</summary>
public sealed record AnkaufbelegContent(
    string Title,
    string Subtitle,
    string DocumentNumber,
    string PurchaseDate,
    IReadOnlyList<BelegSection> Sections,
    IReadOnlyList<string> SignatureLabels,
    string Footer)
{
    /// <summary>All printed text in reading order (for checks and search).</summary>
    public IEnumerable<string> AllText()
    {
        yield return Title;
        yield return Subtitle;
        yield return DocumentNumber;
        yield return PurchaseDate;
        foreach (var section in Sections)
        {
            yield return section.Heading;
            foreach (var field in section.Fields)
            {
                yield return field.Label;
                yield return field.Value;
            }
            foreach (var paragraph in section.Paragraphs)
                yield return paragraph;
        }
        foreach (var label in SignatureLabels)
            yield return label;
        yield return Footer;
    }
}

/// <summary>
/// Purchase receipt ("Ankaufbeleg") for the private seller of a completed intake: who bought what from whom, for how
/// much, with the seller's declaration of ownership and signature lines. The ID document number is masked except for
/// its last four characters. The wording is a template; it makes no claim of legal completeness.
/// </summary>
public static class Ankaufbeleg
{
    public const int VisibleIdCharacters = 4;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>German local time for printed dates (falls back to UTC if the zone is unknown on the host).</summary>
    public static readonly TimeZoneInfo GermanTimeZone = FindGermanTimeZone();

    public static Result<AnkaufbelegContent> Build(Intake intake, Quote quote, BelegDealer dealer, TimeZoneInfo zone)
    {
        if (intake.Status != IntakeStatus.Completed
            || intake is not { PaidMinor: { } paid, PaymentMethod: { } method, CompletedAt: { } completedAt, InspectedCondition: { } condition })
            return Error.Conflict("ankauf.not_completed", "A purchase receipt is only available for a completed intake.");
        if (quote.Id != intake.QuoteId)
            throw new ArgumentException("The quote does not belong to the intake.", nameof(quote));

        var local = TimeZoneInfo.ConvertTime(completedAt, zone);
        var number = DocumentNumber(intake.Id, DateOnly.FromDateTime(local.DateTime));
        var seller = intake.Seller;
        var defects = intake.InspectedDefects.Count == 0 ? "keine festgestellt" : string.Join(", ", intake.InspectedDefects.Select(DefectName));

        var dealerFields = new List<BelegField>
        {
            new("Name", dealer.Name),
        };
        if (!string.Equals(dealer.LegalName, dealer.Name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(dealer.LegalName))
            dealerFields.Add(new("Firma", dealer.LegalName));
        dealerFields.Add(new("Anschrift", Address(dealer.Street, dealer.PostalCode, dealer.City, dealer.CountryCode)));
        if (!string.IsNullOrWhiteSpace(dealer.VatId))
            dealerFields.Add(new("USt-IdNr.", dealer.VatId));

        var deviceFields = new List<BelegField>
        {
            new("Hersteller", quote.Brand),
            new("Modell", quote.Model),
            new("Speicher", string.IsNullOrWhiteSpace(quote.Storage) ? "–" : quote.Storage),
            new("IMEI", intake.Imei),
            new("Zustand (geprüft)", ConditionName(condition)),
            new("Mängel", defects),
        };
        if (intake.InspectedBatteryHealthPercent is { } battery)
            deviceFields.Add(new("Akkuzustand", $"{battery} %"));
        if (!string.IsNullOrWhiteSpace(intake.InspectionNotes))
            deviceFields.Add(new("Prüfvermerk", intake.InspectionNotes));

        var paymentSentence = method == Domain.PaymentMethod.Cash
            ? $"Der Verkäufer / die Verkäuferin bestätigt, den Ankaufpreis von {Euro(paid)} in bar erhalten zu haben."
            : $"Der Ankaufpreis von {Euro(paid)} wird per Überweisung an den Verkäufer / die Verkäuferin gezahlt.";

        var sections = new List<BelegSection>
        {
            new("Käufer (Händler)", dealerFields, []),
            intake.AnonymizedAt is { } anonymizedAt
                ? new("Verkäufer/in (Privatperson)",
                [
                    new("Name", SellerDetails.AnonymizedText),
                    new("Anschrift", SellerDetails.AnonymizedText),
                    new("Geburtsdatum", SellerDetails.AnonymizedText),
                    new("Ausweisdokument", SellerDetails.AnonymizedText),
                    new("Ausweisnummer", SellerDetails.AnonymizedText),
                ],
                [
                    $"Die personenbezogenen Daten des Verkäufers / der Verkäuferin wurden am "
                    + $"{TimeZoneInfo.ConvertTime(anonymizedAt, zone).ToString("dd.MM.yyyy", German)} nach Ablauf der Aufbewahrungsfrist anonymisiert.",
                ])
                : new("Verkäufer/in (Privatperson)",
                [
                    new("Name", $"{seller.FirstName} {seller.LastName}"),
                    new("Anschrift", Address(seller.Street, seller.PostalCode, seller.City, null)),
                    new("Geburtsdatum", seller.BirthDate.ToString("dd.MM.yyyy", German)),
                    new("Ausweisdokument", IdDocumentName(seller.IdDocumentType)),
                    new("Ausweisnummer", MaskIdNumber(seller.IdDocumentNumber)),
                ], []),
            new("Gerät", deviceFields, []),
            new("Ankaufpreis und Zahlung",
            [
                new("Ankaufpreis", Euro(paid)),
                new("Zahlungsart", PaymentName(method)),
            ], [paymentSentence]),
            new("Hinweis zur Umsatzsteuer",
            [],
            [
                "Ankauf von einer Privatperson: Der Ankaufpreis enthält keine gesondert ausgewiesene Umsatzsteuer. "
                + "Der Ankauf wird für die Differenzbesteuerung (§ 25a UStG) dokumentiert.",
            ]),
            new("Eigentumserklärung",
            [],
            [
                "Der Verkäufer / die Verkäuferin erklärt, alleiniger Eigentümer / alleinige Eigentümerin des oben genannten Geräts "
                + "zu sein und darüber frei verfügen zu dürfen. Das Gerät ist frei von Rechten Dritter; es ist insbesondere "
                + "nicht gestohlen, verloren, gemietet, finanziert, verpfändet oder unter Eigentumsvorbehalt gekauft. "
                + "Die Angaben zur Person und zum Gerät sind richtig und vollständig.",
            ]),
        };

        return new AnkaufbelegContent(
            "Ankaufbeleg",
            "Ankauf eines gebrauchten Mobilgeräts von einer Privatperson",
            number,
            local.ToString("dd.MM.yyyy, HH:mm 'Uhr'", German),
            sections,
            ["Ort, Datum, Unterschrift Verkäufer/in", "Ort, Datum, Unterschrift Käufer (Händler)"],
            $"Ankaufbeleg {number} · {dealer.Name}");
    }

    /// <summary>"AB-20261015-1A2B3C4D": purchase date and the random tail of the intake id (the head of a v7 id is a timestamp).</summary>
    public static string DocumentNumber(Guid intakeId, DateOnly date) =>
        $"AB-{date:yyyyMMdd}-{intakeId.ToString("N")[^8..].ToUpperInvariant()}";

    /// <summary>Shows only the last four characters; numbers of four characters or fewer are masked completely.</summary>
    public static string MaskIdNumber(string number)
    {
        var trimmed = number.Trim();
        return trimmed.Length <= VisibleIdCharacters
            ? new string('*', trimmed.Length)
            : new string('*', trimmed.Length - VisibleIdCharacters) + trimmed[^VisibleIdCharacters..];
    }

    public static string Euro(long minor) => (minor / 100m).ToString("#,##0.00", German) + " €";

    public static string ConditionName(DeviceCondition condition) => condition switch
    {
        DeviceCondition.Neu => "Neu",
        DeviceCondition.WieNeu => "Wie neu",
        DeviceCondition.SehrGut => "Sehr gut",
        DeviceCondition.Gut => "Gut",
        DeviceCondition.Akzeptabel => "Akzeptabel",
        _ => condition.ToString(),
    };

    public static string DefectName(DeviceDefect defect) => defect switch
    {
        DeviceDefect.ScreenDamage => "Displayschaden",
        DeviceDefect.HousingDamage => "Gehäuseschaden",
        DeviceDefect.CameraDefect => "Kamera defekt",
        DeviceDefect.BiometricDefect => "Face ID / Fingerabdrucksensor defekt",
        DeviceDefect.MissingFunction => "Funktion fehlt oder eingeschränkt",
        _ => defect.ToString(),
    };

    public static string IdDocumentName(IdDocumentType type) => type switch
    {
        IdDocumentType.IdCard => "Personalausweis",
        IdDocumentType.Passport => "Reisepass",
        IdDocumentType.ResidencePermit => "Aufenthaltstitel",
        _ => type.ToString(),
    };

    public static string PaymentName(PaymentMethod method) => method switch
    {
        Domain.PaymentMethod.Cash => "Barzahlung",
        Domain.PaymentMethod.BankTransfer => "Überweisung",
        _ => method.ToString(),
    };

    private static string Address(string street, string postalCode, string city, string? countryCode) =>
        $"{street}, {postalCode} {city}" + (countryCode is null or "DE" ? string.Empty : $", {countryCode}");

    private static TimeZoneInfo FindGermanTimeZone()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Utc;
    }
}
