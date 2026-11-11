using FluentValidation;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>PointsPerEuro: 0–100 per full euro paid on eligible lines; PointValueMinor: 1–100 cents per point at redemption.</summary>
public sealed record LoyaltySettingsRequest(bool Enabled, int PointsPerEuro, int PointValueMinor, int MinRedeemPoints = 0);

/// <summary>UpdatedAt: null while the organization never saved settings (defaults: disabled).</summary>
public sealed record LoyaltySettingsDto(bool Enabled, int PointsPerEuro, int PointValueMinor, int MinRedeemPoints, DateTimeOffset? UpdatedAt);

public sealed record LoyaltyTransactionDto(Guid Id, string Type, int Points, int BalanceAfter, Guid? ReceiptId, long? ReceiptNumber, string? Reason,
    DateTimeOffset CreatedAt, Guid CreatedBy, string? CreatedByName);

/// <summary>A customer's points with the settings the till needs to offer a redemption, and a page of the history (newest first).</summary>
public sealed record CustomerLoyaltyDto(Guid CustomerId, int Points, bool Enabled, int PointValueMinor, int MinRedeemPoints,
    PagedResult<LoyaltyTransactionDto> Transactions);

/// <summary>Points: signed (positive adds, negative takes); Reason: required.</summary>
public sealed record LoyaltyAdjustmentRequest(int Points, string Reason);

internal sealed class LoyaltySettingsValidator : AbstractValidator<LoyaltySettingsRequest>
{
    public LoyaltySettingsValidator()
    {
        RuleFor(x => x.PointsPerEuro).InclusiveBetween(0, LoyaltySettings.MaxPointsPerEuro);
        RuleFor(x => x.PointValueMinor).InclusiveBetween(1, LoyaltySettings.MaxPointValueMinor);
        RuleFor(x => x.MinRedeemPoints).InclusiveBetween(0, LoyaltySettings.MaxMinRedeemPoints);
    }
}

internal sealed class LoyaltyAdjustmentValidator : AbstractValidator<LoyaltyAdjustmentRequest>
{
    public LoyaltyAdjustmentValidator()
    {
        RuleFor(x => x.Points).InclusiveBetween(-LoyaltyAccount.MaxAdjustment, LoyaltyAccount.MaxAdjustment);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(LoyaltyAccount.MaxReasonLength);
    }
}

/// <summary>
/// Loyalty points (ADR-0020): settings, a customer's balance and history, manual adjustments. Earning and redeeming happen
/// inside the sale transaction (<see cref="SaleService"/>), which locks the account through this service.
/// </summary>
internal sealed class LoyaltyService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IUserAccounts users, IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<LoyaltySettings> SettingsAsync(CancellationToken ct) =>
        await db.Set<LoyaltySettings>().AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == OrganizationId, ct) ?? LoyaltySettings.Default(OrganizationId);

    public async Task<LoyaltySettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var stored = await db.Set<LoyaltySettings>().AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == OrganizationId, ct);
        return ToDto(stored ?? LoyaltySettings.Default(OrganizationId), stored is not null);
    }

    public async Task<Result<LoyaltySettingsDto>> SetSettingsAsync(LoyaltySettingsRequest request, CancellationToken ct)
    {
        var settings = await db.Set<LoyaltySettings>().FirstOrDefaultAsync(s => s.OrganizationId == OrganizationId, ct);
        if (settings is null)
        {
            var created = LoyaltySettings.Create(OrganizationId, request.Enabled, request.PointsPerEuro, request.PointValueMinor, request.MinRedeemPoints,
                actor.UserId, clock.UtcNow);
            if (!created.IsSuccess)
                return created.Error!;
            settings = created.Value;
            db.Add(settings);
        }
        else if (settings.Update(request.Enabled, request.PointsPerEuro, request.PointValueMinor, request.MinRedeemPoints, actor.UserId, clock.UtcNow)
                 is { IsSuccess: false } invalid)
        {
            return invalid.Error!;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            return Error.Conflict("concurrency", "The loyalty settings were changed concurrently. Reload and try again.");
        }
        return ToDto(settings, true);
    }

    public async Task<Result<CustomerLoyaltyDto>> GetForCustomerAsync(Guid customerId, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await CustomerExistsAsync(customerId, ct))
            return Error.NotFound("pos.customer_not_found", "Customer not found.");
        var (p, size) = Paging.Normalize(page, pageSize);
        var settings = await SettingsAsync(ct);
        var points = await db.Set<LoyaltyAccount>().AsNoTracking().Where(a => a.OrganizationId == OrganizationId && a.CustomerId == customerId)
            .Select(a => (int?)a.Points).FirstOrDefaultAsync(ct) ?? 0;
        var query = db.Set<LoyaltyTransaction>().AsNoTracking().Where(t => t.OrganizationId == OrganizationId && t.CustomerId == customerId);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var people = await users.GetAsync(rows.Select(t => t.CreatedBy).Distinct().ToList(), ct);
        var receiptIds = rows.Where(t => t.ReceiptId != null).Select(t => t.ReceiptId!.Value).Distinct().ToList();
        var numbers = await db.Set<Receipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var items = rows.Select(t => new LoyaltyTransactionDto(t.Id, t.Type.ToString(), t.Points, t.BalanceAfter, t.ReceiptId,
            t.ReceiptId is { } r && numbers.TryGetValue(r, out var n) ? n : null, t.Reason, t.CreatedAt, t.CreatedBy,
            people.GetValueOrDefault(t.CreatedBy)?.DisplayName)).ToList();
        return new CustomerLoyaltyDto(customerId, points, settings.Enabled, settings.PointValueMinor, settings.MinRedeemPoints,
            new PagedResult<LoyaltyTransactionDto>(items, p, size, total));
    }

    public async Task<Result<CustomerLoyaltyDto>> AdjustAsync(Guid customerId, LoyaltyAdjustmentRequest request, CancellationToken ct)
    {
        if (!await CustomerExistsAsync(customerId, ct))
            return Error.NotFound("pos.customer_not_found", "Customer not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var account = await LockAccountAsync(customerId, ct);
        var adjusted = account.Adjust(request.Points, request.Reason, actor.UserId, clock.UtcNow);
        if (!adjusted.IsSuccess)
            return adjusted.Error!;
        db.Add(adjusted.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetForCustomerAsync(customerId, null, null, ct);
    }

    /// <summary>Points per customer (customers without an account: absent).</summary>
    public async Task<Dictionary<Guid, int>> PointsAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct) =>
        customerIds.Count == 0
            ? []
            : await db.Set<LoyaltyAccount>().AsNoTracking().Where(a => a.OrganizationId == OrganizationId && customerIds.Contains(a.CustomerId))
                .ToDictionaryAsync(a => a.CustomerId, a => a.Points, ct);

    /// <summary>
    /// The customer's account under a row lock (tracked), created empty on first use. Must run inside the transaction that
    /// changes the points: concurrent sales of one customer queue here.
    /// </summary>
    internal async Task<LoyaltyAccount> LockAccountAsync(Guid customerId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pos.loyalty_accounts (organization_id, customer_id, points, updated_at)
            VALUES ({OrganizationId}, {customerId}, 0, {clock.UtcNow})
            ON CONFLICT (organization_id, customer_id) DO NOTHING
            """, ct);
        return (await db.Set<LoyaltyAccount>()
            .FromSql($"SELECT *, xmin FROM pos.loyalty_accounts WHERE organization_id = {OrganizationId} AND customer_id = {customerId} FOR UPDATE")
            .ToListAsync(ct)).Single();
    }

    private Task<bool> CustomerExistsAsync(Guid customerId, CancellationToken ct) =>
        db.Set<Customer>().AnyAsync(c => c.Id == customerId && c.OrganizationId == OrganizationId, ct);

    private static LoyaltySettingsDto ToDto(LoyaltySettings s, bool stored) =>
        new(s.Enabled, s.PointsPerEuro, s.PointValueMinor, s.MinRedeemPoints, stored ? s.UpdatedAt : null);
}

internal static class LoyaltyEndpoints
{
    public static void Map(RouteGroupBuilder sell, RouteGroupBuilder manage)
    {
        sell.MapGet("/loyalty/settings", async (LoyaltyService service, CancellationToken ct) => Results.Ok(await service.GetSettingsAsync(ct)));

        manage.MapPut("/loyalty/settings", async (LoyaltySettingsRequest request, IValidator<LoyaltySettingsRequest> validator, LoyaltyService service,
                CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.SetSettingsAsync(request, ct)).ToHttp(Results.Ok));

        sell.MapGet("/customers/{id:guid}/loyalty", async (Guid id, int? page, int? pageSize, LoyaltyService service, CancellationToken ct) =>
            (await service.GetForCustomerAsync(id, page, pageSize, ct)).ToHttp(Results.Ok));

        manage.MapPost("/customers/{id:guid}/loyalty/adjustments", async (Guid id, LoyaltyAdjustmentRequest request,
                IValidator<LoyaltyAdjustmentRequest> validator, LoyaltyService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.AdjustAsync(id, request, ct)).ToHttp(Results.Ok));
    }
}
