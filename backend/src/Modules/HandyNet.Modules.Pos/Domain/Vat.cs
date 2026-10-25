using System.Text.Json.Serialization;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>How VAT is computed for a receipt line. Stored as text — never rename without a data migration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaxTreatment>))]
public enum TaxTreatment
{
    /// <summary>Regular 19 % VAT contained in the gross price.</summary>
    Standard19,

    /// <summary>
    /// §25a UStG margin scheme (used device bought VAT-free from a private person): VAT only on the positive margin
    /// (sales price − purchase price), and the VAT must not be shown separately on the receipt.
    /// </summary>
    Margin25a,

    /// <summary>
    /// Not taxable at the till (DSFinV-K USt-Schlüssel 5, 0 %): the settlement of an invoice paid at the till
    /// ("Forderungsauflösung"). The VAT was charged on the invoice; the payment receipt carries none. Such lines have no product.
    /// </summary>
    NotTaxable,
}

/// <summary>
/// VAT contained in gross (consumer) prices, in integer cents. Rounding: half away from zero.
/// Negative amounts (refunds) are computed on the magnitude and negated, so a refund is the exact mirror of a sale.
/// </summary>
public static class VatCalculator
{
    public const int StandardRateBasisPoints = 1900;

    /// <summary>vat = gross − round(gross × 100 / 119).</summary>
    public static long StandardVatFromGross(long grossMinor)
    {
        var magnitude = Math.Abs(grossMinor);
        var net = (long)decimal.Round(magnitude * 10_000m / (10_000m + StandardRateBasisPoints), 0, MidpointRounding.AwayFromZero);
        return Math.Sign(grossMinor) * (magnitude - net);
    }

    /// <summary>§25a: VAT on the positive margin only; a sale at or below the purchase price carries no VAT.</summary>
    public static long MarginVat(long grossMinor, long purchasePriceMinor)
    {
        if (grossMinor < 0 || purchasePriceMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(grossMinor), "Margin VAT is computed for sales; refunds mirror the original line.");
        var margin = grossMinor - purchasePriceMinor;
        return margin > 0 ? StandardVatFromGross(margin) : 0;
    }

    public static long VatFor(TaxTreatment treatment, long grossMinor, long? purchasePriceMinor) => treatment switch
    {
        TaxTreatment.Standard19 => StandardVatFromGross(grossMinor),
        TaxTreatment.NotTaxable => 0,
        TaxTreatment.Margin25a => MarginVat(grossMinor, purchasePriceMinor
                                                       ?? throw new ArgumentNullException(nameof(purchasePriceMinor), "§25a needs the purchase price.")),
        _ => throw new ArgumentOutOfRangeException(nameof(treatment)),
    };

    /// <summary>Standard and §25a lines use the standard rate (on the price or on the margin); not taxable lines 0 %.</summary>
    public static int RateFor(TaxTreatment treatment) => treatment == TaxTreatment.NotTaxable ? 0 : StandardRateBasisPoints;

    /// <summary>VAT contained in a gross amount at any rate: gross − round(gross × 10000 / (10000 + rate)), half away from zero.</summary>
    public static long VatFromGross(long grossMinor, int rateBasisPoints)
    {
        if (rateBasisPoints == 0)
            return 0;
        var magnitude = Math.Abs(grossMinor);
        var net = (long)decimal.Round(magnitude * 10_000m / (10_000m + rateBasisPoints), 0, MidpointRounding.AwayFromZero);
        return Math.Sign(grossMinor) * (magnitude - net);
    }

    /// <summary>VAT on a net amount: round(net × rate / 10000), half away from zero, sign-symmetric.</summary>
    public static long VatOnNet(long netMinor, int rateBasisPoints) =>
        Math.Sign(netMinor) * (long)decimal.Round(Math.Abs(netMinor) * (decimal)rateBasisPoints / 10_000m, 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Share of <paramref name="total"/> for the first <paramref name="part"/> of <paramref name="of"/> units, rounded half away from
    /// zero. Refunding units r+1..r+q as <c>Allocate(total, r+q, of) − Allocate(total, r, of)</c> telescopes to exactly
    /// <paramref name="total"/> once every unit has come back — partial returns can never refund more than was paid.
    /// </summary>
    public static long Allocate(long total, int part, int of)
    {
        if (of <= 0 || part < 0 || part > of)
            throw new ArgumentOutOfRangeException(nameof(part));
        return (long)decimal.Round(total * (decimal)part / of, 0, MidpointRounding.AwayFromZero);
    }
}
