using System.Globalization;

namespace HandyNet.SharedKernel;

/// <summary>
/// Parses numbers as they appear in German and international spreadsheets. Refuses ambiguous input
/// such as "1.234" (thousand separator or decimal point?) rather than guessing an amount of money.
/// </summary>
public static class NumberParser
{
    public static bool TryParseAmount(string input, out decimal value)
    {
        value = 0;
        var s = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '€' && c != ' ').ToArray());
        if (s.EndsWith("EUR", StringComparison.OrdinalIgnoreCase))
            s = s[..^3];
        if (s.Length == 0)
            return false;

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        string normalized;

        if (lastComma >= 0 && lastDot >= 0)
        {
            // Both present: the right-most one is the decimal separator.
            var (thousands, decimalSep) = lastComma > lastDot ? ('.', ',') : (',', '.');
            normalized = s.Replace(thousands.ToString(), string.Empty).Replace(decimalSep, '.');
        }
        else if (lastComma >= 0 || lastDot >= 0)
        {
            var sep = lastComma >= 0 ? ',' : '.';
            var count = s.Count(c => c == sep);
            var digitsAfter = s.Length - s.LastIndexOf(sep) - 1;
            if (count > 1)
                normalized = s.Replace(sep.ToString(), string.Empty); // 1.234.567 → thousands only
            else if (digitsAfter == 3)
                return false; // ambiguous: 1.234 / 1,234
            else
                normalized = s.Replace(sep, '.');
        }
        else
        {
            normalized = s;
        }

        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseInt(string input, out int value)
    {
        value = 0;
        var s = input.Trim();
        // Spreadsheets often export integers as "5.0" / "5,0".
        if (s.EndsWith(".0", StringComparison.Ordinal) || s.EndsWith(",0", StringComparison.Ordinal))
            s = s[..^2];
        return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
