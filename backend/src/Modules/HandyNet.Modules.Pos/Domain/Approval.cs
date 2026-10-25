using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>What an approval was used for. Stored as text.</summary>
public enum ApprovalUse
{
    Sale,
    Storno,
    Return,
    CashMovement,
    CloseSession,

    /// <summary>Credit note (Stornorechnung) of a sales invoice, approved by PIN instead of the cashier's pos.refund right.</summary>
    CreditNote,
}

/// <summary>
/// A manager approval on the spot: an owner or manager entered their PIN at the till to allow actions beyond the
/// cashier's rights (price override, discount above the limit, refund, cash movement, closing). Valid for a short time,
/// for one register, the device login and the cashier who asked, and used exactly once; the receipt, cash movement or
/// closed session it was used for is recorded on the row (append-only apart from that one-time use, audited).
/// </summary>
public sealed class PosApproval : IAuditable
{
    private PosApproval() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }

    /// <summary>The rights granted for one action (subset of <see cref="Permissions.ConfigurablePos"/>).</summary>
    public List<string> Permissions { get; private set; } = [];
    public Guid ApproverUserId { get; private set; }

    /// <summary>The cashier the till acted as when asking.</summary>
    public Guid RequestedByUserId { get; private set; }
    public Guid DeviceUserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public ApprovalUse? UsedFor { get; private set; }
    public Guid? ReceiptId { get; private set; }
    public Guid? CashMovementId { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? SalesInvoiceId { get; private set; }

    /// <summary>
    /// Rules: only POS rights a cashier can lack, at least one; the approver is an owner or manager holding them and not
    /// the cashier asking.
    /// </summary>
    public static Result<PosApproval> Grant(Guid organizationId, Guid registerId, IReadOnlyCollection<string> permissions, Guid approverUserId,
        MembershipRole approverRole, IReadOnlySet<string> approverPermissions, Guid requestedByUserId, Guid deviceUserId, string tokenHash,
        DateTimeOffset now, TimeSpan lifetime)
    {
        var wanted = permissions.Distinct().Order().ToList();
        if (wanted.Count == 0 || wanted.Any(p => !HandyNet.SharedKernel.Tenancy.Permissions.ConfigurablePos.Contains(p)))
            return Error.Validation("pos.approval_permissions",
                $"Approve one or more of: {string.Join(", ", HandyNet.SharedKernel.Tenancy.Permissions.ConfigurablePos.Order())}.");
        if (approverRole is not (MembershipRole.Owner or MembershipRole.Manager))
            return Error.Forbidden("pos.approver_not_manager", "Only an owner or a manager can approve.");
        if (wanted.Any(p => !approverPermissions.Contains(p)))
            return Error.Forbidden("pos.approver_lacks_right", "The approver does not hold the requested rights.");
        if (approverUserId == requestedByUserId)
            return Error.Conflict("pos.approval_self", "A cashier cannot approve their own action.");
        return new PosApproval
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            RegisterId = registerId,
            Permissions = wanted,
            ApproverUserId = approverUserId,
            RequestedByUserId = requestedByUserId,
            DeviceUserId = deviceUserId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    /// <summary>Uses the approval once for the given record.</summary>
    public Result MarkUsed(ApprovalUse use, Guid? receiptId, Guid? cashMovementId, Guid? sessionId, DateTimeOffset now, Guid? salesInvoiceId = null)
    {
        if (!IsUsable(now))
            return Error.Conflict("pos.approval_used", "This approval was already used or has expired. Ask for a new approval.");
        if ((receiptId is null ? 0 : 1) + (cashMovementId is null ? 0 : 1) + (sessionId is null ? 0 : 1) + (salesInvoiceId is null ? 0 : 1) != 1)
            return Error.Validation("pos.approval_target", "An approval is used for exactly one receipt, cash movement or closing.");
        UsedAt = now;
        UsedFor = use;
        ReceiptId = receiptId;
        CashMovementId = cashMovementId;
        SessionId = sessionId;
        SalesInvoiceId = salesInvoiceId;
        return Result.Success();
    }
}
