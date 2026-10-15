using FluentValidation;
using HandyNet.Modules.Disputes.Domain;
using HandyNet.Modules.Ordering.Contracts;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Disputes.Application;

public sealed record OpenDisputeRequest(DisputeReason Reason, string Description);

public sealed record TextRequest(string Text);

public sealed record DisputeMessageDto(string From, string Text, DateTimeOffset SentAt);

public sealed record DisputeDto(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string Role,
    Guid BuyerOrganizationId,
    Guid SellerOrganizationId,
    string Reason,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? EscalatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    string? Resolution,
    IReadOnlyList<DisputeMessageDto> Messages);

public sealed record EscalatedDisputeRow(Guid Id, Guid OrderId, string OrderNumber, Guid BuyerOrganizationId, Guid SellerOrganizationId, string Reason, DateTimeOffset EscalatedAt);

public sealed record EscalatedDisputeDto(Guid Id, Guid OrderId, string OrderNumber, Guid BuyerOrganizationId, string BuyerName,
    Guid SellerOrganizationId, string SellerName, string Reason, DateTimeOffset EscalatedAt);

internal sealed class OpenDisputeValidator : AbstractValidator<OpenDisputeRequest>
{
    public OpenDisputeValidator()
    {
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Description).NotEmpty().MaximumLength(Dispute.MaxTextLength);
    }
}

internal sealed class DisputeService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IOrderFulfilment orders,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<DisputeDto>> OpenAsync(Guid orderId, OpenDisputeRequest request, CancellationToken ct)
    {
        var order = await orders.GetForFulfilmentAsync(orderId, ct);
        if (order is null || order.BuyerOrganizationId != OrganizationId)
            return Error.NotFound("order.not_found", "Order not found.");

        var dispute = Dispute.Open(order.Id, order.Number, order.BuyerOrganizationId, order.SellerOrganizationId, request.Reason, request.Description,
            currentUser.UserId, clock.UtcNow);
        if (!dispute.IsSuccess)
            return dispute.Error!;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var opened = await orders.OpenDisputeAsync(order.Id, ct); // Delivered → Disputed, joins this transaction
        if (!opened.IsSuccess)
            return opened.Error!;
        db.Add(dispute.Value);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("dispute.exists", "There is already an open dispute for this order.");
        }

        return ToDto(dispute.Value);
    }

    public async Task<IReadOnlyList<DisputeDto>> ListAsync(Guid? orderId, CancellationToken ct)
    {
        var query = db.Set<Dispute>().AsNoTracking().Include(d => d.Messages)
            .Where(d => d.BuyerOrganizationId == OrganizationId || d.SellerOrganizationId == OrganizationId);
        if (orderId is { } id)
            query = query.Where(d => d.OrderId == id);
        return (await query.OrderByDescending(d => d.OpenedAt).Take(100).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<Result<DisputeDto>> GetAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, track: false, ct) is { } dispute ? ToDto(dispute) : Error.NotFound("dispute.not_found", "Dispute not found.");

    public Task<Result<DisputeDto>> AddMessageAsync(Guid id, string text, CancellationToken ct) =>
        MutateAsync(id, d => d.AddMessage(PartyOf(d), currentUser.UserId, text, clock.UtcNow), null, ct);

    public Task<Result<DisputeDto>> EscalateAsync(Guid id, string text, CancellationToken ct) =>
        MutateAsync(id, d =>
        {
            var escalated = d.Escalate(clock.UtcNow);
            return escalated.IsSuccess && !string.IsNullOrWhiteSpace(text)
                ? d.AddMessage(PartyOf(d), currentUser.UserId, text, clock.UtcNow)
                : escalated;
        }, null, ct);

    /// <summary>Seller grants a full refund: dispute Refunded, order Refunded (payment refund follows via the outbox).</summary>
    public Task<Result<DisputeDto>> GrantRefundAsync(Guid id, string resolution, CancellationToken ct) =>
        MutateAsync(id, d => PartyOf(d) == DisputeParty.Seller
                ? d.Resolve(DisputeParty.Seller, refund: true, resolution, clock.UtcNow)
                : Error.Forbidden("dispute.not_seller", "Only the seller can grant a refund."),
            refund: true, ct);

    /// <summary>Buyer withdraws: dispute closed without refund, order Completed (payout follows).</summary>
    public Task<Result<DisputeDto>> WithdrawAsync(Guid id, string resolution, CancellationToken ct) =>
        MutateAsync(id, d => PartyOf(d) == DisputeParty.Buyer
                ? d.Resolve(DisputeParty.Buyer, refund: false, resolution, clock.UtcNow)
                : Error.Forbidden("dispute.not_buyer", "Only the buyer can withdraw a dispute."),
            refund: false, ct);

    /// <summary>Platform decision on an escalated dispute. Called in a scope acting as the seller (see DisputeAdministration).</summary>
    public Task<Result<DisputeDto>> DecideAsync(Guid id, bool refund, string resolution, Guid adminUserId, CancellationToken ct) =>
        MutateAsync(id, d => d.Resolve(DisputeParty.Platform, refund, resolution, clock.UtcNow), refund, ct);

    private DisputeParty PartyOf(Dispute d) =>
        d.BuyerOrganizationId == OrganizationId ? DisputeParty.Buyer : DisputeParty.Seller;

    private async Task<Result<DisputeDto>> MutateAsync(Guid id, Func<Dispute, Result> action, bool? refund, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dispute = await LoadAsync(id, track: true, ct);
        if (dispute is null)
            return Error.NotFound("dispute.not_found", "Dispute not found.");

        var result = action(dispute);
        if (!result.IsSuccess)
            return result.Error!;

        if (refund is { } r)
        {
            var closed = await orders.CloseDisputeAsync(dispute.OrderId, r, dispute.Resolution ?? string.Empty, ct);
            if (!closed.IsSuccess)
                return closed.Error!;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The dispute was changed concurrently. Reload and try again.");
        }

        return ToDto(dispute);
    }

    private Task<Dispute?> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var query = db.Set<Dispute>().Include(d => d.Messages)
            .Where(d => d.Id == id && (d.BuyerOrganizationId == OrganizationId || d.SellerOrganizationId == OrganizationId));
        return (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private DisputeDto ToDto(Dispute d) => new(
        d.Id, d.OrderId, d.OrderNumber, d.BuyerOrganizationId == tenant.OrganizationId ? "buyer" : "seller", d.BuyerOrganizationId, d.SellerOrganizationId,
        d.Reason.ToString(), d.Status.ToString(), d.OpenedAt, d.EscalatedAt, d.ResolvedAt, d.ResolvedBy?.ToString(), d.Resolution,
        d.Messages.OrderBy(m => m.SentAt).Select(m => new DisputeMessageDto(m.From.ToString(), m.Text, m.SentAt)).ToList());
}

/// <summary>
/// Platform-admin access to escalated disputes. Admins have no tenant, so an id-only SECURITY DEFINER function lists
/// escalated disputes; reading details and deciding run in a scope acting as the seller organization (ADR-0005).
/// </summary>
public sealed class DisputeAdministration(IServiceScopeFactory scopeFactory, IOrganizationDirectory organizations)
{
    public async Task<IReadOnlyList<EscalatedDisputeDto>> ListEscalatedAsync(CancellationToken ct)
    {
        List<EscalatedDisputeRow> rows;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
            rows = await db.Database.SqlQuery<EscalatedDisputeRow>(
                    $"SELECT id, order_id, order_number, buyer_organization_id, seller_organization_id, reason, escalated_at FROM disputes.escalated_disputes(200)")
                .ToListAsync(ct);
        }

        var names = await organizations.GetAsync(rows.SelectMany(r => new[] { r.BuyerOrganizationId, r.SellerOrganizationId }).Distinct().ToList(), ct);
        string Name(Guid id) => names.GetValueOrDefault(id)?.Name ?? "?";
        return rows.Select(r => new EscalatedDisputeDto(r.Id, r.OrderId, r.OrderNumber, r.BuyerOrganizationId, Name(r.BuyerOrganizationId),
            r.SellerOrganizationId, Name(r.SellerOrganizationId), r.Reason, r.EscalatedAt)).ToList();
    }

    /// <summary>Details for the platform (role "platform"; read through the seller's scope, see class summary).</summary>
    public async Task<Result<DisputeDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var result = await AsSellerAsync(id, service => service.GetAsync(id, ct), ct);
        return result.IsSuccess ? result.Value with { Role = "platform" } : result;
    }

    public Task<Result<DisputeDto>> DecideAsync(Guid id, bool refund, string resolution, Guid adminUserId, CancellationToken ct) =>
        AsSellerAsync(id, service => service.DecideAsync(id, refund, resolution, adminUserId, ct), ct);

    private async Task<Result<DisputeDto>> AsSellerAsync(Guid id, Func<DisputeService, Task<Result<DisputeDto>>> action, CancellationToken ct)
    {
        Guid? seller;
        await using (var lookup = scopeFactory.CreateAsyncScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<HandyNetDbContext>();
            seller = await db.Database.SqlQuery<Guid?>($"SELECT disputes.dispute_seller({id}) AS \"Value\"").SingleAsync(ct);
        }
        if (seller is null)
            return Error.NotFound("dispute.not_found", "Dispute not found.");

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(seller.Value);
        return await action(scope.ServiceProvider.GetRequiredService<DisputeService>());
    }
}
