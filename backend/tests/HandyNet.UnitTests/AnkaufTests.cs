using System.Text.Json;
using ClosedXML.Excel;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Ankauf.Infrastructure;

namespace HandyNet.UnitTests;

public class PriceApprovalTests
{
    [Theory]
    [InlineData(null, 10000, PriceChange.New, EntryDecision.Pending)]
    [InlineData(10000L, 10000, PriceChange.Unchanged, EntryDecision.AutoAccepted)]
    [InlineData(10000L, 10001, PriceChange.Increase, EntryDecision.Pending)]
    [InlineData(10000L, 9000, PriceChange.Decrease, EntryDecision.AutoAccepted)]
    [InlineData(10000L, 7000, PriceChange.Decrease, EntryDecision.AutoAccepted)]      // exactly 30 % → automatic
    [InlineData(10000L, 6999, PriceChange.LargeDecrease, EntryDecision.Pending)]      // just over 30 % → approval
    public void Rules_from_the_specification(long? previous, long next, PriceChange change, EntryDecision decision) =>
        Assert.Equal((change, decision), PriceApproval.Classify(previous, next));
}

public class BuybackCalculatorTests
{
    private static readonly PolicySettings Policy = PolicySettings.Default;

    private static QuoteCalculation Calc(DeviceCondition condition, DeviceDefect[]? defects = null, int? battery = null, long? paid = null,
        PolicySettings? policy = null) =>
        BuybackCalculator.Calculate(300_00, policy ?? Policy, "APPLE", "APPLE|IPHONE13|128GB",
            new QuoteInput(condition, defects ?? [], battery, paid));

    [Theory]
    [InlineData(DeviceCondition.Neu, 360_00)]
    [InlineData(DeviceCondition.WieNeu, 321_00)]
    [InlineData(DeviceCondition.SehrGut, 315_00)]
    [InlineData(DeviceCondition.Gut, 300_00)]
    [InlineData(DeviceCondition.Akzeptabel, 240_00)]
    public void Condition_multipliers_from_the_specification(DeviceCondition condition, long expected) =>
        Assert.Equal(expected, Calc(condition).ReferencePurchasePriceMinor);

    [Fact]
    public void Deductions_add_up_and_include_weak_batteries()
    {
        var calc = Calc(DeviceCondition.Gut, [DeviceDefect.ScreenDamage, DeviceDefect.CameraDefect], battery: 79);
        Assert.Equal(3000 + 1500 + 1000, calc.TotalDeduction);
        Assert.Equal(135_00, calc.ReferencePurchasePriceMinor); // 300 × (1 − 55 %)
        Assert.Equal(1000 + 3000, Calc(DeviceCondition.Gut, [DeviceDefect.HousingDamage, DeviceDefect.ScreenDamage], battery: 80).TotalDeduction);
    }

    [Fact]
    public void Deductions_are_capped_so_prices_never_go_negative()
    {
        var calc = Calc(DeviceCondition.Akzeptabel, Enum.GetValues<DeviceDefect>(), battery: 10);
        Assert.Equal(10_000, calc.TotalDeduction);
        Assert.Equal(0, calc.ReferencePurchasePriceMinor);
    }

    [Fact]
    public void Platform_offer_dealer_maximum_and_margin_are_separate_numbers()
    {
        var policy = Policy with { PlatformMargin = 1000, MinDealerMargin = 1500 };
        var calc = Calc(DeviceCondition.Gut, paid: 200_00, policy: policy);
        Assert.Equal(300_00, calc.ReferencePurchasePriceMinor);
        Assert.Equal(270_00, calc.PlatformOfferMinor);        // −10 % platform margin
        Assert.Equal(229_50, calc.MaxConsumerPriceMinor);     // −15 % minimum dealer margin
        Assert.Equal(70_00, calc.DealerMarginMinor);          // 270 − 200
        Assert.Equal(2593, calc.DealerMarginBasisPoints);     // 25.93 %
    }

    [Fact]
    public void Model_override_beats_brand_override_beats_default()
    {
        var policy = Policy with
        {
            BrandOverrides = new Dictionary<string, IReadOnlyDictionary<DeviceCondition, int>> { ["APPLE"] = new Dictionary<DeviceCondition, int> { [DeviceCondition.Neu] = 1500 } },
            ModelOverrides = new Dictionary<string, IReadOnlyDictionary<DeviceCondition, int>> { ["APPLE|IPHONE13|128GB"] = new Dictionary<DeviceCondition, int> { [DeviceCondition.Neu] = 1000 } },
        };
        Assert.Equal(1000, Calc(DeviceCondition.Neu, policy: policy).ConditionAdjustment);
        Assert.Equal(1500, BuybackCalculator.Calculate(300_00, policy, "APPLE", "APPLE|IPHONE14|128GB",
            new QuoteInput(DeviceCondition.Neu, [], null, null)).ConditionAdjustment);
        Assert.Equal(2000, BuybackCalculator.Calculate(300_00, policy, "SAMSUNG", "SAMSUNG|S23|128GB",
            new QuoteInput(DeviceCondition.Neu, [], null, null)).ConditionAdjustment);
    }

    [Fact]
    public void Seeded_policy_json_matches_the_code_default()
    {
        // Same literal as migration 'Ankauf' — keeps the seed and PolicySettings.Default in sync.
        const string seeded = """
            {"conditionAdjustments": {"Neu": 2000, "WieNeu": 700, "SehrGut": 500, "Gut": 0, "Akzeptabel": -2000},
             "brandOverrides": {}, "modelOverrides": {},
             "defectDeductions": {"ScreenDamage": 3000, "HousingDamage": 1000, "CameraDefect": 1500, "BiometricDefect": 2000, "MissingFunction": 2500},
             "batteryHealthThresholdPercent": 80, "batteryDeduction": 1000, "platformMargin": 0, "minDealerMargin": 1500}
            """;
        var parsed = JsonSerializer.Deserialize<PolicySettings>(seeded, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(PolicySettings.Default.ConditionAdjustments, parsed.ConditionAdjustments);
        Assert.Equal(PolicySettings.Default.DefectDeductions, parsed.DefectDeductions);
        Assert.Equal(PolicySettings.Default with { ConditionAdjustments = parsed.ConditionAdjustments, DefectDeductions = parsed.DefectDeductions,
            BrandOverrides = parsed.BrandOverrides, ModelOverrides = parsed.ModelOverrides }, parsed);
        Assert.Null(parsed.Validate());
    }

    [Fact]
    public void Invalid_policies_are_rejected() =>
        Assert.NotNull((PolicySettings.Default with { MinDealerMargin = 9000 }).Validate());
}

public class ImeiTests
{
    [Theory]
    [InlineData("490154203237518", true)]
    [InlineData("49-015420-323751-8", true)]
    [InlineData("490154203237519", false)]
    [InlineData("12345", false)]
    public void Luhn_check(string input, bool valid) => Assert.Equal(valid, Imei.TryNormalize(input, out _));
}

public class PriceListReaderTests
{
    private static byte[] Workbook(Action<IXLWorksheet> fill, string sheet = "Mobilfunk")
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Info").Cell(1, 1).Value = "Ankaufspreisliste Oktober";
        fill(workbook.AddWorksheet(sheet));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Reads_the_mobilfunk_sheet_below_a_title_and_reports_rows_without_price()
    {
        var bytes = Workbook(s =>
        {
            s.Cell(1, 1).Value = "Ankaufspreise Mobilfunk – Stand 01.10.";
            s.Cell(3, 1).Value = "Hersteller"; s.Cell(3, 2).Value = "Modell"; s.Cell(3, 3).Value = "Speicher"; s.Cell(3, 4).Value = "Ankaufspreis (Gut)";
            s.Cell(4, 1).Value = "Apple"; s.Cell(4, 2).Value = "iPhone 13"; s.Cell(4, 3).Value = "128 GB"; s.Cell(4, 4).Value = 310;
            s.Cell(5, 1).Value = "Apple"; s.Cell(5, 2).Value = "iPhone 13"; s.Cell(5, 3).Value = "256 GB"; s.Cell(5, 4).Value = "345,50 €";
            s.Cell(7, 1).Value = "Samsung"; s.Cell(7, 2).Value = "Galaxy S21"; s.Cell(7, 3).Value = "128 GB"; s.Cell(7, 4).Value = "auf Anfrage";
        });

        var sheet = PriceListReader.Read(bytes, null).Value;
        Assert.Equal("Mobilfunk", sheet.SheetName);
        Assert.Equal([31000L, 34550L], sheet.Rows.Select(r => r.PriceMinor));
        Assert.Equal("128 GB", sheet.Rows[0].Storage);
        var skipped = Assert.Single(sheet.Skipped);
        Assert.Equal(7, skipped.RowNumber);
    }

    [Fact]
    public void Missing_sheet_is_reported_with_available_names() =>
        Assert.Contains("Mobilfunk", PriceListReader.Read(Workbook(_ => { }, sheet: "Tablets"), null).Error!.Message);
}
