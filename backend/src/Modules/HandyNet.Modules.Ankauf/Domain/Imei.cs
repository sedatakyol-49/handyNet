namespace HandyNet.Modules.Ankauf.Domain;

/// <summary>Kept for Ankauf call sites; the rule itself is Inventory's (it owns serialized stock).</summary>
public static class Imei
{
    /// <summary>15 digits with a valid Luhn check digit (spaces, dashes and slashes are ignored).</summary>
    public static bool TryNormalize(string? input, out string imei) => Inventory.Domain.Imei.TryNormalize(input, out imei);
}
