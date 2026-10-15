using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

/// <summary>Device condition grades used for buyback. Stored as text.</summary>
public enum DeviceCondition
{
    Neu,
    WieNeu,
    SehrGut,
    Gut,
    Akzeptabel,
}

/// <summary>Defects that reduce the buyback price. Stored as text.</summary>
public enum DeviceDefect
{
    ScreenDamage,
    HousingDamage,
    CameraDefect,
    BiometricDefect,
    MissingFunction,
}

public enum PriceChange
{
    New,
    Unchanged,
    Increase,
    Decrease,
    LargeDecrease,
}

public enum EntryDecision
{
    AutoAccepted,
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// Approval rules for a new monthly price (the reference price for condition "Gut"):
/// every increase and every new model need an administrator; a decrease of more than 30 % needs an
/// administrator; smaller decreases and unchanged prices are accepted automatically.
/// </summary>
public static class PriceApproval
{
    /// <summary>Decreases above this share (in basis points, 3000 = 30 %) need approval.</summary>
    public const int LargeDecreaseBasisPoints = 3000;

    public static (PriceChange Change, EntryDecision Decision) Classify(long? previousMinor, long newMinor)
    {
        if (previousMinor is not { } previous)
            return (PriceChange.New, EntryDecision.Pending);
        if (newMinor == previous)
            return (PriceChange.Unchanged, EntryDecision.AutoAccepted);
        if (newMinor > previous)
            return (PriceChange.Increase, EntryDecision.Pending);

        // Integer comparison: (previous − new) / previous > 30 %  ⇔  (previous − new) × 10 000 > 30 % × previous.
        var large = (previous - newMinor) * 10_000 > (long)LargeDecreaseBasisPoints * previous;
        return large ? (PriceChange.LargeDecrease, EntryDecision.Pending) : (PriceChange.Decrease, EntryDecision.AutoAccepted);
    }
}

/// <summary>
/// Admin-configurable, versioned pricing parameters. All percentages are basis points (100 = 1 %).
/// A new policy is a new immutable row; quotes keep the id of the policy they were calculated with.
/// </summary>
public sealed record PolicySettings(
    IReadOnlyDictionary<DeviceCondition, int> ConditionAdjustments,
    IReadOnlyDictionary<string, IReadOnlyDictionary<DeviceCondition, int>> BrandOverrides,
    IReadOnlyDictionary<string, IReadOnlyDictionary<DeviceCondition, int>> ModelOverrides,
    IReadOnlyDictionary<DeviceDefect, int> DefectDeductions,
    int BatteryHealthThresholdPercent,
    int BatteryDeduction,
    int PlatformMargin,
    int MinDealerMargin)
{
    /// <summary>The sample values from the specification (to be confirmed by the business).</summary>
    public static PolicySettings Default { get; } = new(
        new Dictionary<DeviceCondition, int>
        {
            [DeviceCondition.Neu] = 2000,
            [DeviceCondition.WieNeu] = 700,
            [DeviceCondition.SehrGut] = 500,
            [DeviceCondition.Gut] = 0,
            [DeviceCondition.Akzeptabel] = -2000,
        },
        new Dictionary<string, IReadOnlyDictionary<DeviceCondition, int>>(),
        new Dictionary<string, IReadOnlyDictionary<DeviceCondition, int>>(),
        new Dictionary<DeviceDefect, int>
        {
            [DeviceDefect.ScreenDamage] = 3000,
            [DeviceDefect.HousingDamage] = 1000,
            [DeviceDefect.CameraDefect] = 1500,
            [DeviceDefect.BiometricDefect] = 2000,
            [DeviceDefect.MissingFunction] = 2500,
        },
        BatteryHealthThresholdPercent: 80,
        BatteryDeduction: 1000,
        PlatformMargin: 0,
        MinDealerMargin: 1500);

    public Error? Validate()
    {
        if (Enum.GetValues<DeviceCondition>().Any(c => !ConditionAdjustments.ContainsKey(c)))
            return Error.Validation("ankauf.policy_conditions", "Every condition needs an adjustment.");
        var all = ConditionAdjustments.Values
            .Concat(BrandOverrides.Values.SelectMany(v => v.Values))
            .Concat(ModelOverrides.Values.SelectMany(v => v.Values));
        if (all.Any(v => v is < -9000 or > 10000))
            return Error.Validation("ankauf.policy_range", "Condition adjustments must be between −90 % and +100 %.");
        if (DefectDeductions.Values.Append(BatteryDeduction).Any(v => v is < 0 or > 10000))
            return Error.Validation("ankauf.policy_range", "Deductions must be between 0 and 100 %.");
        if (PlatformMargin is < 0 or > 5000 || MinDealerMargin is < 0 or > 5000)
            return Error.Validation("ankauf.policy_range", "Margins must be between 0 and 50 %.");
        if (BatteryHealthThresholdPercent is < 0 or > 100)
            return Error.Validation("ankauf.policy_range", "Battery threshold must be a percentage.");
        return null;
    }

    /// <summary>Most specific adjustment wins: model override, then brand override, then the default.</summary>
    public int AdjustmentFor(DeviceCondition condition, string brandKey, string modelKey)
    {
        if (ModelOverrides.TryGetValue(modelKey, out var m) && m.TryGetValue(condition, out var mv))
            return mv;
        if (BrandOverrides.TryGetValue(brandKey, out var b) && b.TryGetValue(condition, out var bv))
            return bv;
        return ConditionAdjustments[condition];
    }
}

public sealed record QuoteInput(DeviceCondition Condition, IReadOnlyCollection<DeviceDefect> Defects, int? BatteryHealthPercent, long? ConsumerPriceMinor);

public sealed record QuoteCalculation(
    long ReferenceGutMinor,
    int ConditionAdjustment,
    int TotalDeduction,
    long ReferencePurchasePriceMinor,
    long PlatformOfferMinor,
    long MaxConsumerPriceMinor,
    long? DealerMarginMinor,
    int? DealerMarginBasisPoints);

/// <summary>
/// Buyback price calculation (all amounts in cents, rounding half away from zero):
/// 1. reference purchase price = Gut price × (1 + condition adjustment) × (1 − sum of deductions), never below 0;
/// 2. platform-to-dealer offer = reference × (1 − platform margin);
/// 3. maximum dealer-to-consumer price = offer × (1 − minimum dealer margin);
/// 4. dealer gross margin = offer − price the dealer pays the consumer (when given).
/// Deductions are added up and capped at 100 %.
/// </summary>
public static class BuybackCalculator
{
    public static QuoteCalculation Calculate(long referenceGutMinor, PolicySettings policy, string brandKey, string modelKey, QuoteInput input)
    {
        var adjustment = policy.AdjustmentFor(input.Condition, brandKey, modelKey);
        var adjusted = Apply(referenceGutMinor, adjustment);

        var deduction = input.Defects.Distinct().Sum(d => policy.DefectDeductions.GetValueOrDefault(d));
        if (input.BatteryHealthPercent is { } health && health < policy.BatteryHealthThresholdPercent)
            deduction += policy.BatteryDeduction;
        deduction = Math.Min(deduction, 10_000);

        var reference = Math.Max(0, Apply(adjusted, -deduction));
        var offer = Apply(reference, -policy.PlatformMargin);
        var maxConsumer = Apply(offer, -policy.MinDealerMargin);

        long? margin = input.ConsumerPriceMinor is { } paid ? offer - paid : null;
        int? marginBp = margin is { } m && offer > 0 ? (int)decimal.Round(m * 10_000m / offer, 0, MidpointRounding.AwayFromZero) : null;
        return new QuoteCalculation(referenceGutMinor, adjustment, deduction, reference, offer, maxConsumer, margin, marginBp);
    }

    private static long Apply(long amount, int basisPoints) =>
        (long)decimal.Round(amount * (10_000m + basisPoints) / 10_000m, 0, MidpointRounding.AwayFromZero);
}

public static class ModelKeys
{
    public static string Of(string brand, string model, string? storage) =>
        $"{Text.NormalizeKey(brand)}|{Text.NormalizeKey(model)}|{Text.NormalizeKey(storage ?? string.Empty)}";
}
