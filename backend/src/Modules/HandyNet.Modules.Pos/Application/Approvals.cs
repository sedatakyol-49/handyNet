using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Permissions: the rights to allow for one action (e.g. ["pos.discount"]).</summary>
public sealed record ApprovalRequest(Guid ApproverUserId, string Pin, IReadOnlyList<string> Permissions);

/// <summary>Returned once: the value for the <c>X-Pos-Approval</c> header of the action it allows.</summary>
public sealed record ApprovalDto(string Token, Guid ApprovalId, DateTimeOffset ExpiresAt, Guid ApproverUserId, string? ApproverName,
    IReadOnlyList<string> Permissions);

/// <summary>Who approved a recorded action (receipt, cash movement, closing).</summary>
public sealed record ApprovalInfoDto(Guid ApprovalId, Guid ApproverUserId, string? ApproverName, IReadOnlyList<string> Permissions, DateTimeOffset ApprovedAt);

internal sealed class ApprovalRequestValidator : AbstractValidator<ApprovalRequest>
{
    public ApprovalRequestValidator()
    {
        RuleFor(x => x.ApproverUserId).NotEmpty();
        RuleFor(x => x.Pin).NotEmpty().MaximumLength(PinPolicy.MaxLength);
        RuleFor(x => x.Permissions).NotEmpty().Must(p => p is null || p.Count <= Permissions.ConfigurablePos.Count);
    }
}

/// <summary>A valid approval presented with the current request.</summary>
internal sealed record ActingApproval(Guid Id, Guid RegisterId, IReadOnlySet<string> Permissions, Guid ApproverUserId, string? ApproverName,
    int MaxDiscountPercent);

/// <summary>
/// Manager approval by PIN (Odoo-style): an owner/manager enters their PIN at the till; the cashier's next write on that
/// register (sale with price override/discount, return, cash movement, closing) runs with the approved rights and records
/// the approval. Single use, short-lived, bound to organization (RLS), register, device login and cashier.
/// </summary>
internal sealed class ApprovalService(
    HandyNetDbContext db,
    ITenantContext tenant,
    IOrganizationDirectory organizations,
    IUserAccounts users,
    CashierService cashiers,
    IOptions<PosOptions> options,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<ApprovalDto>> GrantAsync(Guid registerId, ApprovalRequest request, PosActor actor, CancellationToken ct)
    {
        if (!await db.Set<Register>().AnyAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (actor.CheckRegister(registerId) is { } other)
            return other;
        var approver = await organizations.GetMemberAccessAsync(OrganizationId, request.ApproverUserId, ct);
        if (approver is null)
            return Error.NotFound("pos.approver_not_found", "No member with this id in the organization.");

        var token = OpaqueToken.New();
        var approval = PosApproval.Grant(OrganizationId, registerId, request.Permissions, approver.UserId, approver.Role, approver.Permissions, actor.UserId,
            actor.DeviceUserId, OpaqueToken.HashOf(token), clock.UtcNow, TimeSpan.FromSeconds(options.Value.ApprovalSeconds));
        if (!approval.IsSuccess)
            return approval.Error!;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var verified = await cashiers.VerifyPinAsync(approver.UserId, request.Pin, ct);
        if (!verified.IsSuccess)
        {
            await tx.CommitAsync(ct); // keep the failed attempt (lockout)
            return verified.Error!;
        }
        db.Add(approval.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var name = (await users.GetAsync([approver.UserId], ct)).GetValueOrDefault(approver.UserId)?.DisplayName;
        return new ApprovalDto(token, approval.Value.Id, approval.Value.ExpiresAt, approver.UserId, name, approval.Value.Permissions);
    }

    public async Task<Result<ActingApproval>> ResolveAsync(string token, PosActor actor, CancellationToken ct)
    {
        var invalid = Error.Forbidden("pos.approval_invalid", "The approval is not valid (expired, used, or given for another cashier). Ask again.");
        if (!OpaqueToken.LooksValid(token))
            return invalid;
        var hash = OpaqueToken.HashOf(token);
        var approval = await db.Set<PosApproval>().AsNoTracking().FirstOrDefaultAsync(a => a.TokenHash == hash && a.OrganizationId == OrganizationId, ct);
        if (approval is null || !approval.IsUsable(clock.UtcNow) || approval.DeviceUserId != actor.DeviceUserId || approval.RequestedByUserId != actor.UserId)
            return invalid;
        // The approver must still be an owner/manager of the organization.
        var approver = await organizations.GetMemberAccessAsync(OrganizationId, approval.ApproverUserId, ct);
        if (approver is null || approver.Role is not (MembershipRole.Owner or MembershipRole.Manager))
            return invalid;
        var name = (await users.GetAsync([approver.UserId], ct)).GetValueOrDefault(approver.UserId)?.DisplayName;
        return new ActingApproval(approval.Id, approval.RegisterId, approval.Permissions.ToHashSet(), approver.UserId, name, approver.MaxDiscountPercent);
    }

    /// <summary>
    /// Locks the presented approval (if any) in the current transaction and checks it is still usable — called before the
    /// TSE signs, so a concurrent second use is refused before anything is signed. <see cref="ConsumeAsync"/> then uses it.
    /// </summary>
    public async Task<Error?> LockAsync(PosActor actor, CancellationToken ct)
    {
        if (actor.Approval is not { } presented)
            return null;
        var rows = await db.Set<PosApproval>()
            .FromSql($"SELECT *, xmin FROM pos.approvals WHERE id = {presented.Id} AND organization_id = {OrganizationId} FOR UPDATE").ToListAsync(ct);
        return rows.FirstOrDefault() is { } approval && approval.IsUsable(clock.UtcNow)
            ? null
            : Error.Conflict("pos.approval_used", "This approval was already used or has expired. Ask for a new approval.");
    }

    /// <summary>
    /// Uses the presented approval (if any) for the record written in the current transaction; the row is locked, so a
    /// concurrent second use fails and its transaction must roll back.
    /// </summary>
    public async Task<Error?> ConsumeAsync(PosActor actor, ApprovalUse use, Guid? receiptId, Guid? cashMovementId, Guid? sessionId, CancellationToken ct,
        Guid? salesInvoiceId = null)
    {
        if (actor.Approval is not { } presented)
            return null;
        var rows = await db.Set<PosApproval>()
            .FromSql($"SELECT *, xmin FROM pos.approvals WHERE id = {presented.Id} AND organization_id = {OrganizationId} FOR UPDATE").ToListAsync(ct);
        if (rows.FirstOrDefault() is not { } approval)
            return Error.Conflict("pos.approval_used", "This approval was already used or has expired. Ask for a new approval.");
        var used = approval.MarkUsed(use, receiptId, cashMovementId, sessionId, clock.UtcNow, salesInvoiceId);
        if (!used.IsSuccess)
            return used.Error;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<IReadOnlyDictionary<Guid, ApprovalInfoDto>> ForReceiptsAsync(IReadOnlyCollection<Guid> receiptIds, CancellationToken ct)
    {
        var rows = await db.Set<PosApproval>().AsNoTracking().Where(a => a.ReceiptId != null && receiptIds.Contains(a.ReceiptId.Value)).ToListAsync(ct);
        return await ToInfoAsync(rows, a => a.ReceiptId!.Value, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, ApprovalInfoDto>> ForMovementsAsync(IReadOnlyCollection<Guid> movementIds, CancellationToken ct)
    {
        var rows = await db.Set<PosApproval>().AsNoTracking().Where(a => a.CashMovementId != null && movementIds.Contains(a.CashMovementId.Value))
            .ToListAsync(ct);
        return await ToInfoAsync(rows, a => a.CashMovementId!.Value, ct);
    }

    public async Task<ApprovalInfoDto?> ForClosingAsync(Guid sessionId, CancellationToken ct)
    {
        var rows = await db.Set<PosApproval>().AsNoTracking().Where(a => a.SessionId == sessionId).ToListAsync(ct);
        return (await ToInfoAsync(rows, a => a.SessionId!.Value, ct)).GetValueOrDefault(sessionId);
    }

    private async Task<IReadOnlyDictionary<Guid, ApprovalInfoDto>> ToInfoAsync(List<PosApproval> rows, Func<PosApproval, Guid> key, CancellationToken ct)
    {
        if (rows.Count == 0)
            return new Dictionary<Guid, ApprovalInfoDto>();
        var names = await users.GetAsync(rows.Select(r => r.ApproverUserId).Distinct().ToList(), ct);
        return rows.GroupBy(key).ToDictionary(g => g.Key, g =>
        {
            var a = g.First();
            return new ApprovalInfoDto(a.Id, a.ApproverUserId, names.GetValueOrDefault(a.ApproverUserId)?.DisplayName, a.Permissions, a.UsedAt ?? a.CreatedAt);
        });
    }
}
