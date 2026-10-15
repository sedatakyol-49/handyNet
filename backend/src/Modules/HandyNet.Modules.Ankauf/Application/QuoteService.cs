using FluentValidation;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ankauf.Application;

public sealed record ModelDto(string ModelKey, string Brand, string Model, string? Storage, DateTimeOffset PriceValidUntil);

public sealed record CreateQuoteRequest(
    string ModelKey,
    DeviceCondition Condition,
    DeviceDefect[]? Defects,
    int? BatteryHealthPercent,
    decimal? ConsumerPrice,
    string? Imei);

public sealed record QuoteDto(
    Guid Id,
    string Brand,
    string Model,
    string? Storage,
    string Condition,
    IReadOnlyList<string> Defects,
    int? BatteryHealthPercent,
    string? Imei,
    long ReferenceGutMinor,
    int ConditionAdjustmentBasisPoints,
    int TotalDeductionBasisPoints,
    long ReferencePurchasePriceMinor,
    long PlatformOfferMinor,
    long MaxConsumerPriceMinor,
    long? ConsumerPriceMinor,
    long? DealerMarginMinor,
    Guid PriceVersionId,
    int PolicyNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset ValidUntil,
    string Notice);

internal sealed class CreateQuoteValidator : AbstractValidator<CreateQuoteRequest>
{
    public CreateQuoteValidator()
    {
        RuleFor(x => x.ModelKey).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Condition).IsInEnum();
        RuleForEach(x => x.Defects).IsInEnum();
        RuleFor(x => x.BatteryHealthPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.ConsumerPrice).GreaterThanOrEqualTo(0).PrecisionScale(10, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Imei).Must(i => i is null || Imei.TryNormalize(i, out _)).WithMessage("IMEI must be 15 digits with a valid check digit.");
    }
}

/// <summary>Dealer-facing buyback quotes. Quotes are preliminary; the final payout follows a physical inspection.</summary>
internal sealed class QuoteService(HandyNetDbContext db, ITenantContext tenant, ICurrentUser currentUser, IInventory inventory, IClock clock)
{
    public const string Notice =
        "Vorläufiges Angebot. Die endgültige Auszahlung wird erst nach physischer Prüfung des Geräts bestätigt.";

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>
    /// The list that defines today's prices: the most recently starting published list whose validity covers now.
    /// Only its prices count — a model missing from it (or whose new price was rejected) has no price, even if an
    /// older list still nominally covers today. Prices never silently carry over from a previous month.
    /// </summary>
    private async Task<PriceList?> CurrentListAsync(DateTimeOffset now, CancellationToken ct) =>
        await db.Set<PriceList>().AsNoTracking()
            .Where(l => l.Status == PriceListStatus.Published && l.ValidFrom <= now && l.ValidUntil > now)
            .OrderByDescending(l => l.ValidFrom)
            .ThenByDescending(l => l.PublishedAt) // same start: the most recently published list wins
            .FirstOrDefaultAsync(ct);

    /// <summary>Models with a price in the current list.</summary>
    public async Task<IReadOnlyList<ModelDto>> SearchModelsAsync(string? search, CancellationToken ct)
    {
        if (await CurrentListAsync(clock.UtcNow, ct) is not { } current)
            return [];
        var query = db.Set<PriceVersion>().AsNoTracking().Where(v => v.PriceListId == current.Id);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Text.LikePattern(search);
            query = query.Where(v => EF.Functions.ILike(v.Brand + " " + v.Model + " " + (v.Storage ?? ""), pattern));
        }

        return await query.OrderBy(v => v.Brand).ThenBy(v => v.Model).ThenBy(v => v.Storage).Take(100)
            .Select(v => new ModelDto(v.ModelKey, v.Brand, v.Model, v.Storage, v.ValidUntil))
            .ToListAsync(ct);
    }

    public async Task<Result<QuoteDto>> CreateAsync(CreateQuoteRequest request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var current = await CurrentListAsync(now, ct);
        var price = current is null
            ? null
            : await db.Set<PriceVersion>().AsNoTracking()
                .FirstOrDefaultAsync(v => v.PriceListId == current.Id && v.ModelKey == request.ModelKey, ct);
        if (price is null)
            return Error.Validation("ankauf.no_valid_price",
                "There is no current price for this model. No buyback price can be guaranteed; ask the platform for an individual offer.");

        var policies = await db.Set<ConditionPolicy>().AsNoTracking().Where(p => p.EffectiveFrom <= now).ToListAsync(ct);
        var policy = PriceListService.ActivePolicy(policies, now);
        if (policy is null)
            return Error.Validation("ankauf.no_policy", "No pricing policy is active.");

        long? consumerMinor = null;
        if (request.ConsumerPrice is { } consumer)
        {
            if (!Money.TryFromMajor(consumer, Money.Eur, out var money))
                return Error.Validation("ankauf.consumer_price", "The consumer price may have at most two decimal places.");
            consumerMinor = money.MinorUnits;
        }

        string? imei = null;
        if (request.Imei is { } raw && Imei.TryNormalize(raw, out var normalized))
        {
            if (await inventory.IsImeiInStockAnywhereAsync(normalized, ct))
                return Error.Conflict("unit.imei_in_stock", "This IMEI is already in stock. Do not buy the device; check its origin.");
            imei = normalized;
        }

        var input = new QuoteInput(request.Condition, request.Defects ?? [], request.BatteryHealthPercent, consumerMinor);
        var brandKey = price.ModelKey.Split('|')[0];
        var calculation = BuybackCalculator.Calculate(price.PriceMinor, policy.Settings, brandKey, price.ModelKey, input);

        var quote = Quote.Create(OrganizationId, currentUser.UserId, price, policy, input, calculation, imei, now);
        db.Add(quote);
        await db.SaveChangesAsync(ct);
        return ToDto(quote, policy.Number);
    }

    public async Task<IReadOnlyList<QuoteDto>> ListAsync(CancellationToken ct)
    {
        var rows = await (
                from q in db.Set<Quote>().AsNoTracking()
                join p in db.Set<ConditionPolicy>().AsNoTracking() on q.ConditionPolicyId equals p.Id
                where q.OrganizationId == OrganizationId
                orderby q.CreatedAt descending
                select new { q, p.Number })
            .Take(100)
            .ToListAsync(ct);
        return rows.Select(r => ToDto(r.q, r.Number)).ToList();
    }

    private static QuoteDto ToDto(Quote q, int policyNumber) => new(
        q.Id, q.Brand, q.Model, q.Storage, q.Condition.ToString(), q.Defects.Select(d => d.ToString()).ToList(), q.BatteryHealthPercent, q.Imei,
        q.ReferenceGutMinor, q.ConditionAdjustment, q.TotalDeduction, q.ReferencePurchasePriceMinor, q.PlatformOfferMinor,
        q.MaxConsumerPriceMinor, q.ConsumerPriceMinor, q.DealerMarginMinor, q.PriceVersionId, policyNumber, q.CreatedAt, q.ValidUntil, Notice);
}
