using HandyNet.SharedKernel;

namespace HandyNet.Modules.Disputes.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum DisputeReason
{
    Damaged,
    WrongItem,
    NotAsDescribed,
    MissingItems,
    Defective,
    Other,
}

public enum DisputeStatus
{
    /// <summary>Buyer and seller try to settle it themselves.</summary>
    Open,

    /// <summary>One party asked the platform to decide.</summary>
    Escalated,

    /// <summary>Ended with a full refund to the buyer.</summary>
    Refunded,

    /// <summary>Ended without refund (buyer withdrew, or platform decided for the seller).</summary>
    ClosedWithoutRefund,
}

public enum DisputeParty
{
    Buyer,
    Seller,
    Platform,
}

public sealed record DisputeOpened(Guid DisputeId, Guid OrderId, Guid BuyerOrganizationId, Guid SellerOrganizationId, string Reason) : IDomainEvent;

public sealed record DisputeResolved(Guid DisputeId, Guid OrderId, Guid BuyerOrganizationId, Guid SellerOrganizationId, string Status) : IDomainEvent;

/// <summary>
/// A buyer's complaint about a delivered B2B order, raised within the inspection window. While a dispute is
/// open the order stays <c>Disputed</c>: it does not complete and the seller is not paid out.
/// Outcomes are deliberately simple for now — full refund or no refund; partial refunds and return shipments
/// are on the roadmap.
/// </summary>
public sealed class Dispute : Entity, IAuditable
{
    public const int MaxTextLength = 4000;

    private readonly List<DisputeMessage> _messages = [];

    private Dispute() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string OrderNumber { get; private set; } = null!;
    public Guid BuyerOrganizationId { get; private set; }
    public Guid SellerOrganizationId { get; private set; }
    public DisputeReason Reason { get; private set; }
    public DisputeStatus Status { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public Guid OpenedBy { get; private set; }
    public DateTimeOffset? EscalatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DisputeParty? ResolvedBy { get; private set; }
    public string? Resolution { get; private set; }
    public uint Version { get; private set; }

    public IReadOnlyList<DisputeMessage> Messages => _messages;

    public bool IsActive => Status is DisputeStatus.Open or DisputeStatus.Escalated;

    public static Result<Dispute> Open(Guid orderId, string orderNumber, Guid buyer, Guid seller, DisputeReason reason, string description, Guid userId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > MaxTextLength)
            return Error.Validation("dispute.description", $"Describe the problem (max {MaxTextLength} characters).");

        var dispute = new Dispute
        {
            Id = Guid.CreateVersion7(),
            OrderId = orderId,
            OrderNumber = orderNumber,
            BuyerOrganizationId = buyer,
            SellerOrganizationId = seller,
            Reason = reason,
            Status = DisputeStatus.Open,
            OpenedAt = now,
            OpenedBy = userId,
        };
        dispute._messages.Add(new DisputeMessage(dispute.Id, DisputeParty.Buyer, userId, description.Trim(), now));
        dispute.Raise(new DisputeOpened(dispute.Id, orderId, buyer, seller, reason.ToString()));
        return dispute;
    }

    public Result AddMessage(DisputeParty from, Guid userId, string text, DateTimeOffset now)
    {
        if (!IsActive)
            return Error.Conflict("dispute.closed", "This dispute is closed.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return Error.Validation("dispute.message", $"Messages must have 1–{MaxTextLength} characters.");
        _messages.Add(new DisputeMessage(Id, from, userId, text.Trim(), now));
        return Result.Success();
    }

    public Result Escalate(DateTimeOffset now)
    {
        if (Status != DisputeStatus.Open)
            return Error.Conflict("dispute.not_open", $"A dispute in status {Status} cannot be escalated.");
        Status = DisputeStatus.Escalated;
        EscalatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Ends the dispute. Who may decide what: the seller may only grant a refund; the buyer may only withdraw
    /// (no refund); the platform may decide either way, but only once the dispute was escalated.
    /// </summary>
    public Result Resolve(DisputeParty by, bool refund, string resolution, DateTimeOffset now)
    {
        if (!IsActive)
            return Error.Conflict("dispute.closed", "This dispute is already closed.");
        if (by == DisputeParty.Seller && !refund)
            return Error.Forbidden("dispute.seller_cannot_deny", "Sellers can grant a refund or escalate, not close a dispute against the buyer.");
        if (by == DisputeParty.Buyer && refund)
            return Error.Forbidden("dispute.buyer_cannot_refund", "Buyers can withdraw a dispute; refunds are granted by the seller or the platform.");
        if (by == DisputeParty.Platform && Status != DisputeStatus.Escalated)
            return Error.Conflict("dispute.not_escalated", "The platform decides only escalated disputes.");
        if (string.IsNullOrWhiteSpace(resolution) || resolution.Length > MaxTextLength)
            return Error.Validation("dispute.resolution", "A short explanation of the decision is required.");

        Status = refund ? DisputeStatus.Refunded : DisputeStatus.ClosedWithoutRefund;
        ResolvedAt = now;
        ResolvedBy = by;
        Resolution = resolution.Trim();
        Raise(new DisputeResolved(Id, OrderId, BuyerOrganizationId, SellerOrganizationId, Status.ToString()));
        return Result.Success();
    }
}

public sealed class DisputeMessage
{
    private DisputeMessage() { }

    internal DisputeMessage(Guid disputeId, DisputeParty from, Guid userId, string text, DateTimeOffset at)
    {
        Id = Guid.CreateVersion7();
        DisputeId = disputeId;
        From = from;
        UserId = userId;
        Text = text;
        SentAt = at;
    }

    public Guid Id { get; private set; }
    public Guid DisputeId { get; private set; }
    public DisputeParty From { get; private set; }
    public Guid UserId { get; private set; }
    public string Text { get; private set; } = null!;
    public DateTimeOffset SentAt { get; private set; }
}
