namespace HandyNet.Modules.Ankauf.Domain;

public static class Imei
{
    /// <summary>15 digits with a valid Luhn check digit (spaces, dashes and slashes are ignored).</summary>
    public static bool TryNormalize(string? input, out string imei)
    {
        imei = new string((input ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '/').ToArray());
        if (imei.Length != 15 || !imei.All(char.IsAsciiDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var digit = imei[i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }
            sum += digit;
        }

        return (10 - sum % 10) % 10 == imei[14] - '0';
    }
}
