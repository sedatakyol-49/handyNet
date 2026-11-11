using HandyNet.Modules.Pos.Domain;

namespace HandyNet.UnitTests;

/// <summary>Price lists (ADR-0020): validity, resolution order and rounding of the general discount.</summary>
public class PosPricelistTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static Pricelist List(int discountBp = 0, bool active = true, DateOnly? from = null, DateOnly? to = null) =>
        Pricelist.Create(Org, "Händler", discountBp, active, from, to, Now).Value;

    [Fact]
    public void Item_price_wins_then_the_general_discount_then_the_base()
    {
        var posPrice = new EffectivePrice(20_00, PriceSource.PosPrice);
        var list = List(discountBp: 1000);
        Assert.Equal(new EffectivePrice(15_00, PriceSource.Pricelist), list.Resolve(posPrice, 15_00));
        Assert.Equal(new EffectivePrice(18_00, PriceSource.Pricelist), list.Resolve(posPrice, null));
        // An offer-derived base is discounted the same way; without any base there is nothing to discount.
        Assert.Equal(new EffectivePrice(107_10, PriceSource.Pricelist), list.Resolve(new EffectivePrice(119_00, PriceSource.Offer), null));
        Assert.Equal(EffectivePrice.None, list.Resolve(EffectivePrice.None, null));
        // … but an item price works even without a base price.
        Assert.Equal(new EffectivePrice(9_99, PriceSource.Pricelist), list.Resolve(EffectivePrice.None, 9_99));
        // No general discount and no item: the base, with its own source.
        Assert.Equal(posPrice, List(discountBp: 0).Resolve(posPrice, null));
    }

    [Theory]
    [InlineData(20_00, 1000, 18_00)]
    [InlineData(9_99, 1000, 8_99)]     // discount 99.9 → 100
    [InlineData(9_95, 1000, 8_95)]     // discount 99.5 → 100 (half away from zero)
    [InlineData(9_94, 1000, 8_95)]     // discount 99.4 → 99
    [InlineData(1, 5000, 1)]           // discount 0.5 → 1, but never below one cent
    [InlineData(100_00, 10_000, 1)]    // 100 % still leaves a positive price
    [InlineData(33_33, 333, 32_22)]    // 3.33 %: 110.9889 → 111
    [InlineData(50_00, 1, 49_99)]      // 0.01 %: 0.5 → 1
    public void General_discount_rounds_half_away_from_zero_and_keeps_a_positive_price(long basePrice, int bp, long expected) =>
        Assert.Equal(expected, Pricelist.Discounted(basePrice, bp));

    [Fact]
    public void Applies_only_while_active_and_inside_its_validity()
    {
        Assert.True(List().AppliesOn(Today));
        Assert.False(List(active: false).AppliesOn(Today));
        Assert.True(List(from: Today, to: Today).AppliesOn(Today)); // both ends inclusive
        Assert.False(List(from: Today.AddDays(1)).AppliesOn(Today));
        Assert.False(List(to: Today.AddDays(-1)).AppliesOn(Today));
        Assert.True(List(from: Today.AddDays(-30)).AppliesOn(Today));
        Assert.True(List(to: Today.AddDays(30)).AppliesOn(Today));
    }

    [Fact]
    public void Price_list_and_item_validate_their_fields()
    {
        Assert.Equal("pos.pricelist_name", Pricelist.Create(Org, " ", 0, true, null, null, Now).Error?.Code);
        Assert.Equal("pos.pricelist_name", Pricelist.Create(Org, new string('x', 101), 0, true, null, null, Now).Error?.Code);
        Assert.Equal("pos.pricelist_discount", Pricelist.Create(Org, "A", -1, true, null, null, Now).Error?.Code);
        Assert.Equal("pos.pricelist_discount", Pricelist.Create(Org, "A", 10_001, true, null, null, Now).Error?.Code);
        Assert.Equal("pos.pricelist_validity", Pricelist.Create(Org, "A", 0, true, Today, Today.AddDays(-1), Now).Error?.Code);
        var list = Pricelist.Create(Org, "  Händler  ", 250, true, Today, null, Now).Value;
        Assert.Equal(("Händler", 250), (list.Name, list.DiscountBasisPoints));

        var user = Guid.NewGuid();
        Assert.Equal("pos.price", PricelistItem.Create(list, Guid.NewGuid(), 0, user, Now).Error?.Code);
        Assert.Equal("pos.price", PricelistItem.Create(list, Guid.NewGuid(), PosPricing.MaxPriceMinor + 1, user, Now).Error?.Code);
        var item = PricelistItem.Create(list, Guid.NewGuid(), 12_50, user, Now).Value;
        Assert.Equal((list.Id, Org, 12_50L), (item.PricelistId, item.OrganizationId, item.PriceMinor));
        Assert.Equal("pos.price", item.Change(-5, user, Now).Error?.Code);
        Assert.Equal(12_50, item.PriceMinor);
    }

    [Fact]
    public void A_price_list_price_is_the_till_price_for_the_line_policy()
    {
        var noRights = new CashierRights(false, false, 0);
        var listPrice = List(discountBp: 1000).Resolve(new EffectivePrice(20_00, PriceSource.PosPrice), null);
        // Selling at the list price needs no price-override right; selling at the regular price to this customer would.
        Assert.True(LinePolicy.Check(1, 18_00, 1, 0, listPrice, noRights).IsSuccess);
        var denied = LinePolicy.Check(1, 20_00, 1, 0, listPrice, noRights);
        Assert.Equal("pos.price_override_forbidden", denied.Error?.Code);
        Assert.Equal(("Pricelist", 18_00L), (denied.Error!.Details!["priceSource"], denied.Error.Details["expectedUnitPriceMinor"]));
    }
}
