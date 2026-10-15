using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

public class MoneyTests
{
    [Theory]
    [InlineData("12.34", 1234)]
    [InlineData("0.01", 1)]
    [InlineData("1000", 100000)]
    public void FromMajor_converts_to_cents(string amount, long cents)
    {
        Assert.True(Money.TryFromMajor(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Money.Eur, out var money));
        Assert.Equal(cents, money.MinorUnits);
    }

    [Fact]
    public void FromMajor_rejects_sub_cent_amounts() => Assert.False(Money.TryFromMajor(1.005m, Money.Eur, out _));

    [Theory]
    [InlineData(26970, 1900, 5124)] // 51.243 → 51.24
    [InlineData(250, 1900, 48)]     // 47.5 → 48 (half away from zero)
    [InlineData(100, 700, 7)]
    public void ApplyRate_rounds_half_away_from_zero(long net, int basisPoints, long expected) =>
        Assert.Equal(expected, new Money(net, Money.Eur).ApplyRate(basisPoints).MinorUnits);

    [Fact]
    public void Adding_different_currencies_throws() =>
        Assert.Throws<InvalidOperationException>(() => new Money(1, "EUR") + new Money(1, "USD"));
}

public class GtinTests
{
    [Theory]
    [InlineData("4006381333931", "4006381333931")]
    [InlineData("400638133393 1", "4006381333931")]
    [InlineData("96385074", "0000096385074")]   // GTIN-8 padded
    [InlineData("036000291452", "0036000291452")] // UPC-A padded
    public void Valid_codes_are_normalized(string input, string expected)
    {
        Assert.True(Gtin.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("4006381333932")] // wrong check digit
    [InlineData("123")]
    [InlineData("40063813339A1")]
    public void Invalid_codes_are_rejected(string input) => Assert.False(Gtin.TryNormalize(input, out _));
}

public class ProductMatcherTests
{
    private static readonly ProductRef Display = new(Guid.NewGuid(), "Apple", "iPhone 13 Display", "APPLE", "661-12345", "4006381333931", PartQuality.ServicePack);

    private static ProductKey Key(string? mpn, string? ean, PartQuality quality = PartQuality.ServicePack, string brand = "APPLE") =>
        new(brand, mpn, ean, quality);

    [Fact]
    public void Matches_by_ean() =>
        Assert.Equal(MatchResult.Existing(Display.Id), new ProductMatcher([Display]).Match(Key(null, "4006381333931")));

    [Fact]
    public void Matches_by_brand_mpn_and_quality() =>
        Assert.Equal(MatchResult.Existing(Display.Id), new ProductMatcher([Display]).Match(Key("661-12345", null)));

    [Fact]
    public void Same_mpn_with_different_quality_is_a_different_product() =>
        Assert.Equal(MatchKind.New, new ProductMatcher([Display]).Match(Key("661-12345", null, PartQuality.Aftermarket)).Kind);

    [Fact]
    public void Ean_with_different_quality_is_a_conflict_not_a_merge() =>
        Assert.Equal(MatchKind.Conflict, new ProductMatcher([Display]).Match(Key(null, "4006381333931", PartQuality.Aftermarket)).Kind);

    [Fact]
    public void Ean_with_different_mpn_is_a_conflict() =>
        Assert.Equal(MatchKind.Conflict, new ProductMatcher([Display]).Match(Key("661-99999", "4006381333931")).Kind);

    [Fact]
    public void Mpn_match_with_different_ean_is_a_conflict() =>
        Assert.Equal(MatchKind.Conflict, new ProductMatcher([Display]).Match(Key("661-12345", "0036000291452")).Kind);

    [Fact]
    public void Products_added_later_become_matchable()
    {
        var matcher = new ProductMatcher([]);
        var created = Display with { Id = Guid.NewGuid() };
        matcher.Add(created);
        Assert.Equal(MatchResult.Existing(created.Id), matcher.Match(Key("661-12345", null)));
    }
}

public class StockLevelTests
{
    private static StockLevel Level(int onHand)
    {
        var level = StockLevel.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        level.Adjust(onHand, MovementType.Receipt, null, null, DateTimeOffset.UtcNow);
        return level;
    }

    [Fact]
    public void Adjust_records_a_movement_with_resulting_quantity()
    {
        var level = Level(5);
        var movement = level.Adjust(-2, MovementType.Adjustment, "count", null, DateTimeOffset.UtcNow).Value!;
        Assert.Equal(3, level.OnHand);
        Assert.Equal(-2, movement.Quantity);
        Assert.Equal(3, movement.OnHandAfter);
    }

    [Fact]
    public void Cannot_go_negative() => Assert.False(Level(1).Adjust(-2, MovementType.Adjustment, null, null, DateTimeOffset.UtcNow).IsSuccess);

    [Fact]
    public void Setting_same_quantity_creates_no_movement() =>
        Assert.Null(Level(4).SetOnHand(4, MovementType.ImportSync, null, null, DateTimeOffset.UtcNow).Value);

    [Fact]
    public void Cannot_consume_more_than_reserved() =>
        Assert.False(Level(5).ConsumeReserved(1, "order:x", null, DateTimeOffset.UtcNow).IsSuccess);
}

public class OrderTests
{
    private static readonly Guid Buyer = Guid.NewGuid();
    private static readonly Guid Seller = Guid.NewGuid();

    private static NewOrderLine Line(long cents, int quantity) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "SKU", "Apple", "Display", quantity, new Money(cents, Money.Eur));

    private static Order Place(params NewOrderLine[] lines) =>
        Order.Place(Buyer, Seller, lines, "key", new string('0', 64), null, Guid.NewGuid(), DateTimeOffset.UtcNow).Value;

    [Fact]
    public void Totals_are_computed_in_cents()
    {
        var order = Place(Line(8990, 3), Line(2450, 2));
        Assert.Equal(26970 + 4900, order.NetTotalMinor);
        Assert.Equal(new Money(31870, Money.Eur).ApplyRate(1900).MinorUnits, order.VatTotalMinor);
        Assert.Equal(order.NetTotalMinor + order.VatTotalMinor, order.GrossTotalMinor);
        Assert.Equal([1, 2], order.Lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Cannot_buy_from_yourself() =>
        Assert.False(Order.Place(Buyer, Buyer, [Line(100, 1)], "k", new string('0', 64), null, Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);

    [Fact]
    public void Happy_path_transitions()
    {
        var order = Place(Line(100, 1));
        Assert.True(order.Confirm(DateTimeOffset.UtcNow).IsSuccess);
        Assert.True(order.MarkShipped(DateTimeOffset.UtcNow).IsSuccess);
        Assert.Equal(OrderStatus.Shipped, order.Status);
    }

    [Fact]
    public void Buyer_cannot_cancel_after_confirmation()
    {
        var order = Place(Line(100, 1));
        order.Confirm(DateTimeOffset.UtcNow);
        Assert.False(order.Cancel(null, DateTimeOffset.UtcNow).IsSuccess);
    }

    [Fact]
    public void Cannot_ship_unconfirmed_or_reject_shipped()
    {
        var order = Place(Line(100, 1));
        Assert.False(order.MarkShipped(DateTimeOffset.UtcNow).IsSuccess);
        order.Confirm(DateTimeOffset.UtcNow);
        order.MarkShipped(DateTimeOffset.UtcNow);
        Assert.False(order.Reject("too late", DateTimeOffset.UtcNow).IsSuccess);
    }

    [Fact]
    public void State_changes_raise_events_for_the_outbox()
    {
        var order = Place(Line(100, 1));
        order.Confirm(DateTimeOffset.UtcNow);
        Assert.Contains(order.DomainEvents, e => e is OrderPlaced);
        Assert.Contains(order.DomainEvents, e => e is OrderStatusChanged { To: "Confirmed" });
    }
}
