using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

/// <summary>ICAO 9303 machine-readable zone formats.</summary>
public enum MrzFormat
{
    /// <summary>ID cards (German Personalausweis, Aufenthaltstitel): 3 lines × 30 characters.</summary>
    Td1,

    /// <summary>Older ID cards/visas: 2 lines × 36 characters.</summary>
    Td2,

    /// <summary>Passports (Reisepass): 2 lines × 44 characters.</summary>
    Td3,
}

public sealed record MrzData(
    MrzFormat Format,
    string DocumentCode,
    IdDocumentType DocumentType,
    string IssuingState,
    string DocumentNumber,
    string Surname,
    string GivenNames,
    string Nationality,
    DateOnly BirthDate,
    string? Sex,
    DateOnly ExpiryDate);

/// <summary>
/// Parser for the machine-readable zone of ID documents (ICAO 9303 TD1/TD2/TD3) with all check digits: document number, date
/// of birth, expiry date, optional/personal number (TD3) and the composite check digit. Accepts the text as typed by a
/// keyboard-wedge document scanner (lines separated by line breaks, or all lines without separator) or pasted by hand.
/// Names in the MRZ are transliterated (Ä → AE, ß → SS); they must be compared with the printed name.
/// </summary>
public static class Mrz
{
    private static readonly int[] Weights = [7, 3, 1];

    /// <summary>ICAO check digit (weights 7-3-1; 0–9 = value, A–Z = 10–35, &lt; = 0).</summary>
    public static int CheckDigit(string value)
    {
        var sum = 0;
        for (var i = 0; i < value.Length; i++)
            sum += CharValue(value[i]) * Weights[i % 3];
        return sum % 10;
    }

    private static int CharValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        '<' => 0,
        _ => throw new ArgumentException($"Invalid MRZ character '{c}'."),
    };

    /// <summary>Splits and normalizes MRZ text into its lines (upper case, spaces removed).</summary>
    public static IReadOnlyList<string> Lines(string text)
    {
        var lines = text.ToUpperInvariant().Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => new string(l.Where(c => !char.IsWhiteSpace(c)).ToArray()))
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 1)
        {
            var single = lines[0];
            return single.Length switch
            {
                90 => [single[..30], single[30..60], single[60..]],
                88 => [single[..44], single[44..]],
                72 => [single[..36], single[36..]],
                _ => lines,
            };
        }
        return lines;
    }

    public static Result<MrzData> Parse(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Error.Validation("ankauf.mrz_format", "The machine-readable zone is empty.");
        var lines = Lines(text);
        if (lines.Any(l => l.Any(c => c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '<'))))
            return Error.Validation("ankauf.mrz_format", "The machine-readable zone may only contain A–Z, 0–9 and '<'.");
        return lines.Select(l => l.Length).ToArray() switch
        {
            [30, 30, 30] => ParseTd1(lines, today),
            [36, 36] => ParseTd2Or3(lines, MrzFormat.Td2, today),
            [44, 44] => ParseTd2Or3(lines, MrzFormat.Td3, today),
            _ => Error.Validation("ankauf.mrz_format",
                "Not a machine-readable zone: expected 3 lines of 30 (ID card), 2 of 36 or 2 of 44 characters (passport)."),
        };
    }

    private static Result<MrzData> ParseTd1(IReadOnlyList<string> l, DateOnly today)
    {
        var (line1, line2, line3) = (l[0], l[1], l[2]);
        var code = line1[..2];
        if (code[0] is not ('I' or 'A' or 'C'))
            return Error.Validation("ankauf.mrz_format", "A 3-line MRZ must start with I, A or C (ID card or residence permit).");

        // Document numbers longer than 9 characters continue in the optional data, followed by their check digit (ICAO 9303-5).
        string number;
        string numberWithCheck;
        if (line1[14] == '<' && line1[13] != '<')
        {
            var optional = line1[15..30];
            var end = optional.IndexOf('<');
            if (end < 1)
                return Error.Validation("ankauf.mrz_format", "The extended document number is incomplete.");
            number = line1[5..14] + optional[..(end - 1)];
            numberWithCheck = number + optional[end - 1];
        }
        else
        {
            number = line1[5..14];
            numberWithCheck = line1[5..15];
        }
        if (Check(numberWithCheck, "documentNumber") is { } numberError)
            return numberError;

        var dates = Dates(line2[..7], line2[8..15], today);
        if (!dates.IsSuccess)
            return dates.Error!;
        var composite = line1[5..30] + line2[..7] + line2[8..15] + line2[18..29];
        if (Check(composite + line2[29], "composite") is { } compositeError)
            return compositeError;

        var (surname, given) = Names(line3);
        return new MrzData(MrzFormat.Td1, code.TrimEnd('<'), DocumentType(code), Code(line1[2..5]), number.Replace("<", "", StringComparison.Ordinal),
            surname, given, Code(line2[15..18]), dates.Value.Birth, Sex(line2[7]), dates.Value.Expiry);
    }

    private static Result<MrzData> ParseTd2Or3(IReadOnlyList<string> l, MrzFormat format, DateOnly today)
    {
        var (line1, line2) = (l[0], l[1]);
        var code = line1[..2];
        if (format == MrzFormat.Td3 && code[0] != 'P')
            return Error.Validation("ankauf.mrz_format", "A 2-line MRZ of 44 characters must start with P (passport).");
        if (format == MrzFormat.Td2 && code[0] is not ('I' or 'A' or 'C' or 'P'))
            return Error.Validation("ankauf.mrz_format", "A 2-line MRZ of 36 characters must start with I, A, C or P.");
        if (Check(line2[..10], "documentNumber") is { } numberError)
            return numberError;
        var dates = Dates(line2[13..20], line2[21..28], today);
        if (!dates.IsSuccess)
            return dates.Error!;
        string composite;
        char compositeDigit;
        if (format == MrzFormat.Td3)
        {
            // Personal number: its check digit may be '<' when the field is empty.
            var personal = line2[28..42];
            var personalCheck = line2[42];
            if (!(personalCheck == '<' && personal.All(c => c == '<')) && Check(personal + personalCheck, "personalNumber") is { } personalError)
                return personalError;
            composite = line2[..10] + line2[13..20] + line2[21..43];
            compositeDigit = line2[43];
        }
        else
        {
            composite = line2[..10] + line2[13..20] + line2[21..35];
            compositeDigit = line2[35];
        }
        if (Check(composite + compositeDigit, "composite") is { } compositeError)
            return compositeError;

        var (surname, given) = Names(line1[5..]);
        return new MrzData(format, code.TrimEnd('<'), DocumentType(code), Code(line1[2..5]), line2[..9].Replace("<", "", StringComparison.Ordinal),
            surname, given, Code(line2[10..13]), dates.Value.Birth, Sex(line2[20]), dates.Value.Expiry);
    }

    /// <summary>Verifies a field whose last character is its check digit.</summary>
    private static Error? Check(string fieldWithDigit, string field)
    {
        var digit = fieldWithDigit[^1];
        if (digit is < '0' or > '9' || CheckDigit(fieldWithDigit[..^1]) != digit - '0')
            return Error.Validation("ankauf.mrz_check_digit", $"The check digit of the {field} is wrong; re-read the machine-readable zone.")
                .WithDetails(new Dictionary<string, object?> { ["field"] = field });
        return null;
    }

    private static Result<(DateOnly Birth, DateOnly Expiry)> Dates(string birthWithCheck, string expiryWithCheck, DateOnly today)
    {
        if (Check(birthWithCheck, "birthDate") is { } birthError)
            return birthError;
        if (Check(expiryWithCheck, "expiryDate") is { } expiryError)
            return expiryError;
        // Birth: the most recent century that is not in the future. Expiry: 20YY (documents are valid ≤ 10 years).
        var birth = Date(birthWithCheck[..6], yy => 2000 + yy > today.Year ? 1900 + yy : 2000 + yy);
        var expiry = Date(expiryWithCheck[..6], yy => 2000 + yy);
        if (birth is null || expiry is null)
            return Error.Validation("ankauf.mrz_format", "A date in the machine-readable zone is not a valid calendar date.");
        return (birth.Value, expiry.Value);
    }

    private static DateOnly? Date(string yymmdd, Func<int, int> year)
    {
        if (!yymmdd.All(char.IsAsciiDigit))
            return null;
        var (yy, mm, dd) = (int.Parse(yymmdd[..2]), int.Parse(yymmdd[2..4]), int.Parse(yymmdd[4..6]));
        var y = year(yy);
        return mm is >= 1 and <= 12 && dd >= 1 && dd <= DateTime.DaysInMonth(y, mm) ? new DateOnly(y, mm, dd) : null;
    }

    private static (string Surname, string Given) Names(string field)
    {
        var parts = field.TrimEnd('<').Split("<<", 2);
        static string Clean(string s) => string.Join(' ', s.Split('<', StringSplitOptions.RemoveEmptyEntries));
        return (Clean(parts[0]), parts.Length > 1 ? Clean(parts[1]) : string.Empty);
    }

    private static string Code(string value) => value.Replace("<", "", StringComparison.Ordinal);

    private static string? Sex(char c) => c is 'M' or 'F' or 'X' ? c.ToString() : null;

    /// <summary>P = passport; A…, IR, RP = residence permit (German eAT: "AR"); other I/C = ID card.</summary>
    public static IdDocumentType DocumentType(string code) => code switch
    {
        _ when code.StartsWith('P') => IdDocumentType.Passport,
        _ when code.StartsWith('A') || code is "IR" or "RP" => IdDocumentType.ResidencePermit,
        _ => IdDocumentType.IdCard,
    };

    /// <summary>
    /// Whether the MRZ belongs to the seller data entered (document number, type, birth and expiry date). Used by the API to
    /// confirm data that was captured from the MRZ and possibly edited afterwards.
    /// </summary>
    public static bool Matches(MrzData mrz, SellerDetails seller) =>
        string.Equals(Text.NormalizeKey(seller.IdDocumentNumber), mrz.DocumentNumber, StringComparison.Ordinal)
        && seller.IdDocumentType == mrz.DocumentType
        && seller.BirthDate == mrz.BirthDate
        && seller.IdExpiryDate == mrz.ExpiryDate;
}
