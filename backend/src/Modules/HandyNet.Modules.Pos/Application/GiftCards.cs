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
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// A gift card issued by a receipt. Code: the full code (<c>XXXX-XXXX-XXXX-XXXX</c>) only in the response of the request
/// that created it — null on every later read and on Idempotency-Key replays.
/// </summary>
public sealed record IssuedGiftCardDto(Guid Id, string? Code, string CodeLast4, long AmountMinor, DateOnly? ExpiresOn);

/// <summary>What the till sees after scanning a code. Usable: active and not expired.</summary>
public sealed record GiftCardLookupDto(Guid Id, string CodeLast4, long BalanceMinor, string Status, DateOnly? ExpiresOn, bool Usable, long InitialMinor);

public sealed record GiftCardTransactionDto(Guid Id, string Type, long AmountMinor, long BalanceAfterMinor, Guid? ReceiptId, long? ReceiptNumber, string? Reason,
    DateTimeOffset CreatedAt, Guid CreatedBy, string? CreatedByName);

/// <summary>Transactions: only on the detail read.</summary>
public sealed record GiftCardDto(
    Guid Id,
    string CodeLast4,
    long InitialMinor,
    long BalanceMinor,
    string Status,
    DateTimeOffset IssuedAt,
    Guid IssuedReceiptId,
    long? IssuedReceiptNumber,
    DateOnly? ExpiresOn,
    bool Expired,
    Guid? CustomerId,
    string? CustomerName,
    uint Version,
    IReadOnlyList<GiftCardTransactionDto>? Transactions = null);

public sealed record GiftCardReasonRequest(string Reason);

/// <summary>Code: the card's new code, returned once.</summary>
public sealed record GiftCardReissuedDto(GiftCardDto Card, string Code);

internal sealed class GiftCardReasonValidator : AbstractValidator<GiftCardReasonRequest>
{
    public GiftCardReasonValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(GiftCard.MaxReasonLength);
}

/// <summary>
/// Gift cards (ADR-0020). The till looks a code up (<c>pos.sell</c>, rate-limited); managers list, void and reissue cards.
/// Selling and redeeming happen inside the sale transaction (<see cref="SaleService"/>), which uses the helpers here.
/// </summary>
internal sealed class GiftCardService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IUserAccounts users, IClock clock,
    IOptions<PosOptions> options)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    internal static readonly Error NotFound = Error.NotFound("pos.gift_card_not_found", "No gift card with this code.");

    public GiftCardRules Rules => new(options.Value.GiftCard.MinMinor, options.Value.GiftCard.MaxMinor, options.Value.GiftCard.ValidityYears);

    public DateOnly Today => BerlinTime.Today(clock.UtcNow);

    public DateOnly NewExpiry() => GiftCard.ExpiryFor(Today, Rules.ValidityYears);

    public async Task<Result<GiftCardLookupDto>> LookupAsync(string? code, CancellationToken ct)
    {
        if (GiftCardCode.Normalize(code) is not { } normalized)
            return NotFound;
        var hash = GiftCardCode.HashOf(normalized);
        var card = await db.Set<GiftCard>().AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == OrganizationId && c.CodeHash == hash, ct);
        return card is null
            ? NotFound
            : new GiftCardLookupDto(card.Id, card.CodeLast4, card.BalanceMinor, card.Status.ToString(), card.ExpiresOn, card.IsUsable(Today), card.InitialMinor);
    }

    /// <summary>Newest first. Search: a full code, or the last four characters.</summary>
    public async Task<Result<PagedResult<GiftCardDto>>> ListAsync(string? search, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<GiftCard>().AsNoTracking().Where(c => c.OrganizationId == OrganizationId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<GiftCardStatus>(status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Error.Validation("pos.gift_card_status", $"Unknown status '{status}'.");
            query = query.Where(c => c.Status == parsed);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (GiftCardCode.Normalize(search) is { } normalized)
            {
                var hash = GiftCardCode.HashOf(normalized);
                query = query.Where(c => c.CodeHash == hash);
            }
            else
            {
                var last4 = new string(search.Where(c => c is not (' ' or '-' or '•')).Select(char.ToUpperInvariant).ToArray());
                last4 = last4.Length > 4 ? last4[^4..] : last4;
                query = query.Where(c => c.CodeLast4 == last4);
            }
        }
        var total = await query.CountAsync(ct);
        var cards = await query.OrderByDescending(c => c.IssuedAt).ThenByDescending(c => c.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<GiftCardDto>(await ToDtosAsync(cards, ct), p, size, total);
    }

    public async Task<Result<GiftCardDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var card = await db.Set<GiftCard>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId, ct);
        if (card is null)
            return Error.NotFound("pos.gift_card_not_found", "Gift card not found.");
        return await DetailAsync(card, ct);
    }

    public async Task<Result<GiftCardDto>> VoidAsync(Guid id, GiftCardReasonRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var card = await LockByIdAsync(id, ct);
        if (card is null)
            return Error.NotFound("pos.gift_card_not_found", "Gift card not found.");
        var voided = card.Void(request.Reason, null, actor.UserId, clock.UtcNow);
        if (!voided.IsSuccess)
            return voided.Error!;
        db.Add(voided.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DetailAsync(card, ct);
    }

    public async Task<Result<GiftCardReissuedDto>> ReissueAsync(Guid id, GiftCardReasonRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var card = await LockByIdAsync(id, ct);
        if (card is null)
            return Error.NotFound("pos.gift_card_not_found", "Gift card not found.");
        var code = GiftCardCode.New();
        var reissued = card.Reissue(code, request.Reason, actor.UserId, clock.UtcNow);
        if (!reissued.IsSuccess)
            return reissued.Error!;
        db.Add(reissued.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new GiftCardReissuedDto(await DetailAsync(card, ct), GiftCardCode.Format(code));
    }

    // ---- used by the sale transaction ---------------------------------------------------------------------------------------

    /// <summary>The tenant's card with this code hash under a row lock (tracked): concurrent redemptions of one card queue here.</summary>
    internal async Task<GiftCard?> LockByHashAsync(string codeHash, CancellationToken ct) =>
        (await db.Set<GiftCard>()
            .FromSql($"SELECT *, xmin FROM pos.gift_cards WHERE organization_id = {OrganizationId} AND code_hash = {codeHash} FOR UPDATE").ToListAsync(ct))
        .FirstOrDefault();

    internal async Task<GiftCard?> LockByIdAsync(Guid id, CancellationToken ct) =>
        (await db.Set<GiftCard>()
            .FromSql($"SELECT *, xmin FROM pos.gift_cards WHERE organization_id = {OrganizationId} AND id = {id} FOR UPDATE").ToListAsync(ct))
        .FirstOrDefault();

    /// <summary>Cards issued by a receipt (sold on it, or refunded onto by it), without codes.</summary>
    internal async Task<IReadOnlyList<IssuedGiftCardDto>> IssuedByAsync(Guid receiptId, CancellationToken ct) =>
        (await db.Set<GiftCard>().AsNoTracking().Where(c => c.IssuedReceiptId == receiptId).OrderBy(c => c.Id).ToListAsync(ct))
        .Select(c => new IssuedGiftCardDto(c.Id, null, c.CodeLast4, c.InitialMinor, c.ExpiresOn)).ToList();

    private async Task<GiftCardDto> DetailAsync(GiftCard card, CancellationToken ct)
    {
        var transactions = await db.Set<GiftCardTransaction>().AsNoTracking().Where(t => t.GiftCardId == card.Id).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(ct);
        var people = await users.GetAsync(transactions.Select(t => t.CreatedBy).Distinct().ToList(), ct);
        var receiptIds = transactions.Where(t => t.ReceiptId != null).Select(t => t.ReceiptId!.Value).Distinct().ToList();
        var numbers = await db.Set<Receipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var dto = (await ToDtosAsync([card], ct))[0];
        return dto with
        {
            Transactions = transactions.Select(t => new GiftCardTransactionDto(t.Id, t.Type.ToString(), t.AmountMinor, t.BalanceAfterMinor, t.ReceiptId,
                t.ReceiptId is { } r && numbers.TryGetValue(r, out var n) ? n : null, t.Reason, t.CreatedAt, t.CreatedBy,
                people.GetValueOrDefault(t.CreatedBy)?.DisplayName)).ToList(),
        };
    }

    private async Task<List<GiftCardDto>> ToDtosAsync(IReadOnlyList<GiftCard> cards, CancellationToken ct)
    {
        var receiptIds = cards.Select(c => c.IssuedReceiptId).Distinct().ToList();
        var numbers = await db.Set<Receipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var customerIds = cards.Where(c => c.CustomerId != null).Select(c => c.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var today = Today;
        return cards.Select(c => new GiftCardDto(c.Id, c.CodeLast4, c.InitialMinor, c.BalanceMinor, c.Status.ToString(), c.IssuedAt, c.IssuedReceiptId,
            numbers.TryGetValue(c.IssuedReceiptId, out var n) ? n : null, c.ExpiresOn, c.IsExpired(today), c.CustomerId,
            c.CustomerId is { } id ? customers.GetValueOrDefault(id) : null, c.Version)).ToList();
    }
}

internal static class GiftCardEndpoints
{
    public static void Map(RouteGroupBuilder sell, RouteGroupBuilder manage)
    {
        sell.MapGet("/gift-cards/lookup", async (string? code, GiftCardService service, CancellationToken ct) =>
                (await service.LookupAsync(code, ct)).ToHttp(Results.Ok))
            .RequireRateLimiting(PosModule.GiftCardLookupRateLimitPolicy);

        manage.MapGet("/gift-cards", async (string? search, string? status, int? page, int? pageSize, GiftCardService service, CancellationToken ct) =>
            (await service.ListAsync(search, status, page, pageSize, ct)).ToHttp(Results.Ok));

        manage.MapGet("/gift-cards/{id:guid}", async (Guid id, GiftCardService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapPost("/gift-cards/{id:guid}/void", async (Guid id, GiftCardReasonRequest request, IValidator<GiftCardReasonRequest> validator,
                GiftCardService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.VoidAsync(id, request, ct)).ToHttp(Results.Ok));

        manage.MapPost("/gift-cards/{id:guid}/reissue", async (Guid id, GiftCardReasonRequest request, IValidator<GiftCardReasonRequest> validator,
                GiftCardService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ReissueAsync(id, request, ct)).ToHttp(Results.Ok));
    }
}
