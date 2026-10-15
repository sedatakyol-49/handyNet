namespace HandyNet.SharedKernel;

/// <summary>
/// Monetary amount in integer minor units (cents). Never use floating point for money.
/// </summary>
public readonly record struct Money(long MinorUnits, string Currency)
{
    public const string Eur = "EUR";

    public static Money Zero(string currency = Eur) => new(0, currency);

    public static Money operator +(Money a, Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException($"Currency mismatch: {a.Currency} vs {b.Currency}");
        return new Money(checked(a.MinorUnits + b.MinorUnits), a.Currency);
    }

    public Money Multiply(int quantity) => new(checked(MinorUnits * quantity), Currency);

    /// <summary>Applies a rate given in basis points (1900 = 19%), rounding half away from zero.</summary>
    public Money ApplyRate(int basisPoints)
    {
        var result = decimal.Round(MinorUnits * (decimal)basisPoints / 10_000m, 0, MidpointRounding.AwayFromZero);
        return new Money(checked((long)result), Currency);
    }

    /// <summary>Converts a major-unit amount (e.g. 12.34) to minor units; rejects sub-cent precision.</summary>
    public static bool TryFromMajor(decimal amount, string currency, out Money money)
    {
        var minor = amount * 100m;
        if (minor != decimal.Truncate(minor) || minor > long.MaxValue || minor < long.MinValue)
        {
            money = default;
            return false;
        }

        money = new Money((long)minor, currency);
        return true;
    }

    public override string ToString() => $"{MinorUnits / 100m:0.00} {Currency}";
}
