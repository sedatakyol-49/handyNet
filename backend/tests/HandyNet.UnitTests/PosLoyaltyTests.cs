using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

/// <summary>Loyalty points (ADR-0020): settings, account rules, allocation of a redemption across lines, VAT and refunds.</summary>
public class PosLoyaltyTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static LoyaltySettings Settings(bool enabled = true, int perEuro = 1, int value = 1, int min = 0) =>
        LoyaltySettings.Create(Org, enabled, perEuro, value, min, User, Now).Value;

    private static LoyaltyAccount Account(int points)
    {
        var account = LoyaltyAccount.Open(Org, Guid.NewGuid(), Now);
        if (points > 0)
            Assert.True(account.Adjust(points, "Startguthaben", User, Now).IsSuccess);
        return account;
    }

    [Fact]
    public void Settings_validate_their_ranges_and_default_to_disabled()
    {
        Assert.Equal("pos.loyalty_settings", LoyaltySettings.Create(Org, true, -1, 1, 0, User, Now).Error?.Code);
        Assert.Equal("pos.loyalty_settings", LoyaltySettings.Create(Org, true, 101, 1, 0, User, Now).Error?.Code);
        Assert.Equal("pos.loyalty_settings", LoyaltySettings.Create(Org, true, 1, 0, 0, User, Now).Error?.Code);
        Assert.Equal("pos.loyalty_settings", LoyaltySettings.Create(Org, true, 1, 101, 0, User, Now).Error?.Code);
        Assert.Equal("pos.loyalty_settings", LoyaltySettings.Create(Org, true, 1, 1, -1, User, Now).Error?.Code);
        var defaults = LoyaltySettings.Default(Org);
        Assert.Equal((false, 0, 1, 0), (defaults.Enabled, defaults.PointsPerEuro, defaults.PointValueMinor, defaults.MinRedeemPoints));
        Assert.Equal(0, defaults.PointsFor(1000_00));
    }

    [Theory]
    [InlineData(99, 1, 0)]         // not a full euro
    [InlineData(1_00, 1, 1)]
    [InlineData(19_99, 1, 19)]     // floor
    [InlineData(19_99, 3, 57)]
    [InlineData(0, 5, 0)]
    [InlineData(-10_00, 5, 0)]
    [InlineData(100_00, 0, 0)]
    public void Points_are_earned_per_full_euro(long eligibleGross, int perEuro, int expected) =>
        Assert.Equal(expected, Settings(perEuro: perEuro).PointsFor(eligibleGross));

    [Fact]
    public void Redemption_needs_the_programme_a_customer_and_the_minimum()
    {
        var settings = Settings(value: 5, min: 100);
        Assert.Equal(new LoyaltyRedemption(200, 10_00), settings.Redemption(200, hasCustomer: true).Value);
        Assert.Equal(new LoyaltyRedemption(100, 5_00), settings.Redemption(100, true).Value);
        var belowMin = settings.Redemption(99, true);
        Assert.Equal(("pos.loyalty_min_points", (ErrorKind?)ErrorKind.Validation, 100), (belowMin.Error?.Code, belowMin.Error?.Kind, belowMin.Error?.Details?["minRedeemPoints"]));
        Assert.Equal("pos.loyalty_customer_required", settings.Redemption(200, hasCustomer: false).Error?.Code);
        Assert.Equal("pos.loyalty_points", settings.Redemption(0, true).Error?.Code);
        var disabled = Settings(enabled: false).Redemption(200, true);
        Assert.Equal(("pos.loyalty_disabled", (ErrorKind?)ErrorKind.Conflict), (disabled.Error?.Code, disabled.Error?.Kind));
    }

    [Fact]
    public void Account_never_goes_below_zero_and_records_what_really_happened()
    {
        var account = Account(100);
        var receipt = Guid.NewGuid();
        var tooMany = account.Redeem(101, receipt, User, Now);
        Assert.Equal(("pos.loyalty_insufficient_points", (ErrorKind?)ErrorKind.Conflict, 100), (tooMany.Error?.Code, tooMany.Error?.Kind, tooMany.Error?.Details?["points"]));
        Assert.Equal(100, account.Points);
        var redeemed = account.Redeem(60, receipt, User, Now).Value;
        Assert.Equal((LoyaltyTransactionType.Redeem, -60, 40, receipt), (redeemed.Type, redeemed.Points, redeemed.BalanceAfter, redeemed.ReceiptId!.Value));

        var earned = account.Earn(15, receipt, User, Now)!;
        Assert.Equal((LoyaltyTransactionType.Earn, 15, 55), (earned.Type, earned.Points, earned.BalanceAfter));
        Assert.Null(account.Earn(0, receipt, User, Now));

        // Storno: redeemed points come back, earned points are taken — but never more than the balance.
        var back = account.ReverseRedeem(60, receipt, User, Now)!;
        Assert.Equal((LoyaltyTransactionType.ReverseRedeem, 60, 115), (back.Type, back.Points, back.BalanceAfter));
        var taken = account.ReverseEarn(200, receipt, User, Now)!;
        Assert.Equal((LoyaltyTransactionType.ReverseEarn, -115, 0), (taken.Type, taken.Points, taken.BalanceAfter));
        Assert.Null(account.ReverseEarn(10, receipt, User, Now)); // nothing left to take
        Assert.Equal(0, account.Points);

        // Manual adjustments: signed, with a reason, never below zero.
        Assert.Equal("pos.reason", account.Adjust(10, " ", User, Now).Error?.Code);
        Assert.Equal("pos.loyalty_points", account.Adjust(0, "x", User, Now).Error?.Code);
        Assert.Equal("pos.loyalty_insufficient_points", account.Adjust(-1, "Korrektur", User, Now).Error?.Code);
        var adjusted = account.Adjust(25, "Kulanz", User, Now).Value;
        Assert.Equal((LoyaltyTransactionType.Adjust, 25, 25, "Kulanz"), (adjusted.Type, adjusted.Points, adjusted.BalanceAfter, adjusted.Reason));
        Assert.True(account.Adjust(-25, "Korrektur", User, Now).IsSuccess);
        Assert.Equal(0, account.Points);
    }

    [Theory]
    [InlineData(10_00, new long[] { 19_00, 1_00 }, new long[] { 9_50, 50 })]
    [InlineData(1_00, new long[] { 3_33, 3_33, 3_34 }, new long[] { 33, 33, 34 })]
    [InlineData(1, new long[] { 5_00, 5_00, 5_00 }, new long[] { 1, 0, 0 })]            // tie: line order
    [InlineData(2, new long[] { 5_00, 5_00, 5_00 }, new long[] { 1, 1, 0 })]
    [InlineData(10, new long[] { 1, 1, 98 }, new long[] { 0, 0, 10 })]                  // remainders .1, .1, .8 → the last line
    [InlineData(15_00, new long[] { 10_00, 5_00 }, new long[] { 10_00, 5_00 })]         // everything
    [InlineData(0, new long[] { 10_00, 5_00 }, new long[] { 0, 0 })]
    [InlineData(7, new long[] { 0, 10 }, new long[] { 0, 7 })]                          // a zero line gets nothing
    public void Redemption_is_spread_pro_rata_with_the_largest_remainder(long total, long[] lines, long[] expected)
    {
        var parts = LoyaltyMath.Allocate(total, lines);
        Assert.Equal(expected, parts);
        Assert.Equal(total, parts.Sum());
        Assert.All(parts.Zip(lines), p => Assert.InRange(p.First, 0, p.Second));
    }

    [Fact]
    public void Allocation_always_adds_up_and_never_exceeds_a_line()
    {
        var random = new Random(20261111);
        for (var run = 0; run < 2000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 8)).Select(_ => (long)random.Next(0, 500_00)).ToList();
            var total = random.NextInt64(0, lines.Sum() + 1);
            var parts = LoyaltyMath.Allocate(total, lines);
            Assert.Equal(total, parts.Sum());
            for (var i = 0; i < lines.Count; i++)
                Assert.InRange(parts[i], 0, lines[i]);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => LoyaltyMath.Allocate(11, [5, 5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoyaltyMath.Allocate(-1, [5, 5]));
    }

    [Fact]
    public void Redeemed_points_reduce_line_gross_and_vat_but_not_gift_cards()
    {
        // Cable 2 × 10.00 − 1.00 = 19.00, phone (§25a, bought for 220.00) 300.00, gift card 25.00. Redeem 31.90 on 319.00 eligible.
        var gift = new SaleLineInput(null, null, null, "Geschenkgutschein ••••-AB12", 1, 25_00, 0, TaxTreatment.NotTaxable, null, null, Guid.NewGuid());
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(), PosReceiptTests.UsedPhone(), gift],
            [new PaymentInput(PaymentMethod.Cash, 400_00)], loyalty: new LoyaltyRedemption(3190, 31_90)).Value;

        Assert.Equal([1_90L, 30_00L, 0L], sale.Lines.Select(l => l.LoyaltyDiscountMinor));
        Assert.Equal([17_10L, 270_00L, 25_00L], sale.Lines.Select(l => l.LineGrossMinor));
        Assert.Equal(1_00, sale.Lines[0].DiscountMinor); // the cashier's discount is untouched
        // VAT follows the reduced gross: 19 % out of 17.10, and for §25a out of the reduced margin 270.00 − 220.00.
        Assert.Equal(VatCalculator.StandardVatFromGross(17_10), sale.Lines[0].VatMinor);
        Assert.Equal(VatCalculator.StandardVatFromGross(50_00), sale.Lines[1].VatMinor);
        Assert.Equal(0, sale.Lines[2].VatMinor);
        Assert.All(sale.Lines, l => Assert.Equal(l.UnitPriceMinor * l.Quantity - l.DiscountMinor - l.LoyaltyDiscountMinor, l.LineGrossMinor));
        Assert.All(sale.Lines, l => Assert.Equal(l.LineGrossMinor, l.NetMinor + l.VatMinor));
        Assert.Equal((312_10L, 3190, 31_90L, 287_10L), (sale.TotalGrossMinor, sale.LoyaltyPointsRedeemed, sale.LoyaltyDiscountMinor, sale.LoyaltyEligibleGrossMinor));
        Assert.Equal(400_00 - 312_10, sale.ChangeMinor); // a price reduction, not a payment
        Assert.Equal([PaymentMethod.Cash], sale.Payments.Select(p => p.Method));

        // More than the eligible lines are worth is refused; the gift card does not count.
        var tooMuch = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(), gift], [new PaymentInput(PaymentMethod.Cash, 50_00)],
            loyalty: new LoyaltyRedemption(1901, 19_01));
        Assert.Equal(("pos.loyalty_exceeds_total", 19_00L), (tooMuch.Error?.Code, tooMuch.Error?.Details?["eligibleTotalMinor"]));
        // A sale reduced to nothing is not a sale.
        Assert.Equal("pos.total_not_positive",
            Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()], [], loyalty: new LoyaltyRedemption(1900, 19_00)).Error?.Code);
    }

    [Fact]
    public void A_sale_without_redemption_is_exactly_as_before()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(), PosReceiptTests.UsedPhone()], [new PaymentInput(PaymentMethod.Cash, 319_00)]).Value;
        Assert.All(sale.Lines, l => Assert.Equal(0, l.LoyaltyDiscountMinor));
        Assert.Equal((0, 0L, 0, null as int?), (sale.LoyaltyPointsRedeemed, sale.LoyaltyDiscountMinor, sale.LoyaltyPointsEarned, sale.LoyaltyBalanceAfter));
        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        var document = ReceiptDocument.For(sale, new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null), "Kasse", null, fiscal);
        Assert.Null(document.Loyalty);
        Assert.DoesNotContain(document.Notes, n => n.Contains("Treuepunkte") || n.Contains("Punktestand"));
        var storno = Receipt.CreateStorno(PosReceiptTests.Header(), sale, false, "Fehlbon").Value;
        Assert.Equal(sale.Lines.Select(l => -l.DiscountMinor), storno.Lines.Select(l => l.DiscountMinor));
    }

    [Fact]
    public void Refunds_mirror_the_reduced_lines_and_partial_returns_add_up()
    {
        // 3 cables at 10.00 with 1.00 discount and 0.50 from points: 28.50.
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(3, 10_00, 1_00)], [new PaymentInput(PaymentMethod.Cash, 28_50)],
            loyalty: new LoyaltyRedemption(50, 50)).Value;
        sale.SetLoyalty(28, 78);
        Assert.Equal((28_50L, 50L), (sale.Lines[0].LineGrossMinor, sale.Lines[0].LoyaltyDiscountMinor));

        var storno = Receipt.CreateStorno(PosReceiptTests.Header(), sale, false, "Fehlbon").Value;
        var mirrored = storno.Lines.Single();
        Assert.Equal((-28_50L, -1_00L, -50L, -sale.Lines[0].VatMinor), (mirrored.LineGrossMinor, mirrored.DiscountMinor, mirrored.LoyaltyDiscountMinor, mirrored.VatMinor));
        Assert.Equal(-50, storno.LoyaltyDiscountMinor);
        storno.SetLoyalty(-28, 50, redeemedReversed: 50);
        Assert.Equal((-50, -28, 50), (storno.LoyaltyPointsRedeemed, storno.LoyaltyPointsEarned, storno.LoyaltyBalanceAfter));

        // Three single returns: gross, discount and loyalty share each add up to the original line.
        var returns = new List<Receipt>();
        for (var i = 0; i < 3; i++)
            returns.Add(Receipt.CreateReturn(PosReceiptTests.Header(), sale, new Dictionary<int, int> { [1] = 1 }, new Dictionary<int, int> { [1] = i },
                PaymentMethod.Cash, "defekt").Value);
        Assert.Equal(-28_50, returns.Sum(r => r.Lines.Single().LineGrossMinor));
        Assert.Equal(-1_00, returns.Sum(r => r.Lines.Single().DiscountMinor));
        Assert.Equal(-50, returns.Sum(r => r.Lines.Single().LoyaltyDiscountMinor));
        Assert.Equal(-sale.Lines[0].VatMinor, returns.Sum(r => r.Lines.Single().VatMinor));
        Assert.All(returns, r => Assert.Equal(r.Lines.Single().UnitPriceMinor * r.Lines.Single().Quantity - r.Lines.Single().DiscountMinor - r.Lines.Single().LoyaltyDiscountMinor,
            r.Lines.Single().LineGrossMinor));
    }

    [Theory]
    [InlineData(28, 28_50, 0, 9_50, 9)]        // a third of 28 points → 9.33 → 9
    [InlineData(28, 28_50, 9_50, 9_50, 9)]     // cumulative 18.67 → 18, minus the 9 already taken
    [InlineData(28, 28_50, 19_00, 9_50, 10)]   // the rest: 28 in total
    [InlineData(28, 28_50, 0, 28_50, 28)]
    [InlineData(0, 28_50, 0, 9_50, 0)]
    [InlineData(28, 0, 0, 9_50, 0)]
    public void Returns_take_earned_points_back_pro_rata(int earned, long eligible, long before, long now, int expected) =>
        Assert.Equal(expected, LoyaltyMath.EarnedToReverse(earned, eligible, before, now));

    [Fact]
    public void Receipt_prints_points_redeemed_earned_and_the_balance()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()], [new PaymentInput(PaymentMethod.Cash, 20_00)],
            loyalty: new LoyaltyRedemption(100, 1_00)).Value;
        sale.SetLoyalty(18, 418);
        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        var document = ReceiptDocument.For(sale, new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null), "Kasse", null, fiscal);
        Assert.Equal(new DocumentLoyalty(100, 1_00, 18, 418), document.Loyalty);
        Assert.Equal(1_00, document.Lines.Single().LoyaltyDiscountMinor);
        Assert.Contains("Treuepunkte eingelöst: 100 (Preisnachlass 1,00 €)", document.Notes);
        Assert.Contains("Treuepunkte gesammelt: 18", document.Notes);
        Assert.Contains("Punktestand: 418", document.Notes);
        // Once issued, the figures are final.
        sale.Seal(fiscal, document);
        Assert.Equal("pos.receipt_sealed", sale.SetLoyalty(0, 0).Error?.Code);
    }

    [Fact]
    public void Fingerprint_changes_only_when_points_are_redeemed()
    {
        var register = Guid.NewGuid();
        var product = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var request = new CreateSaleRequest(register, [new SaleLineRequest(product, null, 2, 10_00, 1_00)], [new SalePaymentRequest(PaymentMethod.Cash, 20_00)], customer);
        var legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"sale#{register:N}#{product:N}::2:1000:100#Cash:2000#c:{customer:N}")));
        Assert.Equal(legacy, SaleService.HashRequest(request));
        var redeeming = SaleService.HashRequest(request with { RedeemPoints = 100 });
        Assert.NotEqual(legacy, redeeming);
        Assert.NotEqual(redeeming, SaleService.HashRequest(request with { RedeemPoints = 101 }));
    }
}
