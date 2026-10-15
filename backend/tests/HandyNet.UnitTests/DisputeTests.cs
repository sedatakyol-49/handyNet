using HandyNet.Modules.Disputes.Domain;

namespace HandyNet.UnitTests;

public class DisputeRulesTests
{
    private static Dispute NewDispute() =>
        Dispute.Open(Guid.NewGuid(), "B2B-001000", Guid.NewGuid(), Guid.NewGuid(), DisputeReason.Damaged, "Riss im Display", Guid.NewGuid(), DateTimeOffset.UnixEpoch).Value;

    [Fact]
    public void Seller_can_only_refund_and_buyer_can_only_withdraw()
    {
        Assert.Equal("dispute.seller_cannot_deny", NewDispute().Resolve(DisputeParty.Seller, false, "nein", DateTimeOffset.UnixEpoch).Error!.Code);
        Assert.Equal("dispute.buyer_cannot_refund", NewDispute().Resolve(DisputeParty.Buyer, true, "ja", DateTimeOffset.UnixEpoch).Error!.Code);
        Assert.True(NewDispute().Resolve(DisputeParty.Seller, true, "ok", DateTimeOffset.UnixEpoch).IsSuccess);
        Assert.True(NewDispute().Resolve(DisputeParty.Buyer, false, "geklärt", DateTimeOffset.UnixEpoch).IsSuccess);
    }

    [Fact]
    public void Platform_decides_only_escalated_disputes()
    {
        var dispute = NewDispute();
        Assert.Equal("dispute.not_escalated", dispute.Resolve(DisputeParty.Platform, false, "x", DateTimeOffset.UnixEpoch).Error!.Code);
        dispute.Escalate(DateTimeOffset.UnixEpoch);
        Assert.True(dispute.Resolve(DisputeParty.Platform, false, "Schaden nicht belegt", DateTimeOffset.UnixEpoch).IsSuccess);
        Assert.Equal(DisputeStatus.ClosedWithoutRefund, dispute.Status);
    }

    [Fact]
    public void Closed_disputes_accept_no_messages_or_decisions()
    {
        var dispute = NewDispute();
        dispute.Resolve(DisputeParty.Seller, true, "ok", DateTimeOffset.UnixEpoch);
        Assert.False(dispute.AddMessage(DisputeParty.Buyer, Guid.NewGuid(), "noch was", DateTimeOffset.UnixEpoch).IsSuccess);
        Assert.False(dispute.Resolve(DisputeParty.Buyer, false, "x", DateTimeOffset.UnixEpoch).IsSuccess);
    }
}
