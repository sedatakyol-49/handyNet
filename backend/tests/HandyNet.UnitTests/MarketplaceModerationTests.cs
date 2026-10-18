using HandyNet.Modules.Marketplace.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

public sealed class MarketplaceModerationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static Offer NewOffer(bool publish = true) =>
        Offer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SKU-1",
            new OfferTerms(new Money(1990, Money.Eur), 1, 2, OfferCondition.New), publish, Now).Value;

    /// <summary>Moderation is written only by the database function; rehydrate a blocked offer like EF would.</summary>
    private static Offer Blocked(Offer offer)
    {
        typeof(Offer).GetProperty(nameof(Offer.Moderation))!.SetValue(offer, ModerationStatus.Blocked);
        typeof(Offer).GetProperty(nameof(Offer.ModerationReason))!.SetValue(offer, "Fälschung");
        return offer;
    }

    [Fact]
    public void New_offers_are_not_moderated()
    {
        var offer = NewOffer();
        Assert.Equal(ModerationStatus.None, offer.Moderation);
        Assert.False(offer.IsBlocked);
        Assert.Null(offer.ModerationReason);
    }

    [Theory]
    [InlineData(OfferStatus.Paused)]
    [InlineData(OfferStatus.Archived)]
    [InlineData(OfferStatus.Published)]
    public void A_blocked_offer_cannot_change_its_status(OfferStatus target)
    {
        var offer = Blocked(NewOffer(publish: false));
        var result = offer.ChangeStatus(target, Now);
        Assert.Equal("offer.blocked", result.Error?.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error?.Kind);
        Assert.Equal(OfferStatus.Draft, offer.Status);
    }

    [Fact]
    public void A_blocked_offer_keeps_terms_editable_but_stays_blocked()
    {
        var offer = Blocked(NewOffer());
        Assert.True(offer.UpdateTerms(new OfferTerms(new Money(2490, Money.Eur), 2, 3, OfferCondition.New), Now).IsSuccess);
        Assert.Equal(2490, offer.PriceMinor);
        Assert.True(offer.IsBlocked);
    }

    [Fact]
    public void An_unblocked_offer_changes_status_as_before()
    {
        var offer = NewOffer();
        Assert.True(offer.ChangeStatus(OfferStatus.Paused, Now).IsSuccess);
        Assert.Equal(OfferStatus.Paused, offer.Status);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Fälschung  ", "Fälschung")]
    public void Reasons_are_trimmed_and_optional(string? input, string? expected)
    {
        var result = OfferModeration.NormalizeReason(input);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Reasons_longer_than_the_limit_are_rejected()
    {
        Assert.True(OfferModeration.NormalizeReason(new string('x', OfferModeration.MaxReasonLength)).IsSuccess);
        Assert.Equal("moderation.reason_too_long", OfferModeration.NormalizeReason(new string('x', OfferModeration.MaxReasonLength + 1)).Error?.Code);
    }

    [Fact]
    public void Batches_are_distinct_non_empty_and_bounded()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(new[] { a, b }, OfferModeration.NormalizeBatch([a, b, a, Guid.Empty]).Value);
        Assert.Equal("moderation.no_offers", OfferModeration.NormalizeBatch([]).Error?.Code);
        Assert.Equal("moderation.no_offers", OfferModeration.NormalizeBatch(null).Error?.Code);
        Assert.Equal("moderation.no_offers", OfferModeration.NormalizeBatch([Guid.Empty]).Error?.Code);
        Assert.True(OfferModeration.NormalizeBatch(Enumerable.Range(0, OfferModeration.MaxBatchSize).Select(_ => Guid.NewGuid())).IsSuccess);
        Assert.Equal("moderation.too_many_offers",
            OfferModeration.NormalizeBatch(Enumerable.Range(0, OfferModeration.MaxBatchSize + 1).Select(_ => Guid.NewGuid())).Error?.Code);
    }
}
