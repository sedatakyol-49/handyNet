using HandyNet.SharedKernel;

namespace HandyNet.Modules.Shipping.Domain;

/// <summary>
/// What a carrier product accepts. Defaults are conservative placeholders — the real values depend on the
/// seller's carrier contract and must be configured per environment (Shipping:Carriers:&lt;name&gt;).
/// </summary>
public sealed class CarrierProfile
{
    public int MaxWeightGrams { get; set; } = 31_500;
    public int MaxLengthCm { get; set; } = 120;

    /// <summary>Value covered without extra insurance (carrier's standard liability).</summary>
    public long StandardLiabilityMinor { get; set; } = 500_00;

    /// <summary>Highest value that can be insured at all for this product.</summary>
    public long MaxInsuredValueMinor { get; set; } = 2_500_00;

    /// <summary>Devices with built-in lithium batteries (phones, tablets).</summary>
    public bool AcceptsLithiumInEquipment { get; set; } = true;

    /// <summary>Loose replacement batteries — often restricted or requiring a special contract.</summary>
    public bool AcceptsStandaloneLithiumBatteries { get; set; }
}

public sealed record ShipmentCheck(Parcel Parcel, long DeclaredValueMinor, bool Insured, bool HasDevicesWithBattery, bool HasStandaloneBatteries);

/// <summary>Pure validation of a shipment request against the carrier profile (unit-tested).</summary>
public static class ShippingRules
{
    public static Error? Validate(Carrier carrier, CarrierProfile profile, ShipmentCheck check)
    {
        var p = check.Parcel;
        if (p.WeightGrams <= 0 || p.LengthCm <= 0 || p.WidthCm <= 0 || p.HeightCm <= 0)
            return Error.Validation("shipment.parcel_invalid", "Weight and dimensions must be positive.");
        if (p.WeightGrams > profile.MaxWeightGrams)
            return Error.Validation("shipment.too_heavy", $"{carrier} accepts at most {profile.MaxWeightGrams / 1000m:0.#} kg per parcel.");
        if (Math.Max(p.LengthCm, Math.Max(p.WidthCm, p.HeightCm)) > profile.MaxLengthCm)
            return Error.Validation("shipment.too_long", $"{carrier} accepts at most {profile.MaxLengthCm} cm length.");

        if (check.HasStandaloneBatteries && !profile.AcceptsStandaloneLithiumBatteries)
            return Error.Validation("shipment.batteries_not_allowed",
                $"{carrier} is not configured for loose lithium batteries. Use a carrier/product approved for dangerous goods.");
        if (check.HasDevicesWithBattery && !profile.AcceptsLithiumInEquipment)
            return Error.Validation("shipment.lithium_not_allowed", $"{carrier} is not configured for devices with lithium batteries.");

        if (check.DeclaredValueMinor > profile.MaxInsuredValueMinor)
            return Error.Validation("shipment.value_too_high",
                $"The goods value exceeds what {carrier} can insure ({profile.MaxInsuredValueMinor / 100m:0.00} €). Split the shipment or use a valuables service.");
        if (check.DeclaredValueMinor > profile.StandardLiabilityMinor && !check.Insured)
            return Error.Validation("shipment.insurance_required",
                $"Goods worth more than {profile.StandardLiabilityMinor / 100m:0.00} € must be shipped insured.");
        return null;
    }
}
