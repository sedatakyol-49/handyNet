using HandyNet.SharedKernel.Web;
using FluentValidation;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Marketplace.Application;

/// <summary>Filters of the platform moderation list. Brand and category match exactly (case-insensitive).</summary>
public sealed record AdminOfferQuery(
    Guid? SellerId,
    string? Brand,
    string? Category,
    string? Status,
    bool? Blocked,
    string? Search,
    int? Page,
    int? PageSize);

/// <param name="Blocked">True when the offer is hidden by the platform (offer block or seller block).</param>
/// <param name="BlockedBy">"offer" (this offer is blocked), "seller" (only the whole seller is blocked) or null.</param>
public sealed record AdminOfferDto(
    Guid OfferId,
    Guid SellerOrganizationId,
    string SellerName,
    string SellerCity,
    Guid ProductId,
    string Brand,
    string ProductName,
    string Category,
    string? Ean,
    string? Mpn,
    string SupplierSku,
    long PriceMinor,
    string Currency,
    string Status,
    string Moderation,
    string? ModerationReason,
    DateTimeOffset? ModeratedAt,
    Guid? ModeratedBy,
    bool SellerBlocked,
    string? SellerBlockReason,
    bool Blocked,
    string? BlockedBy,
    DateTimeOffset UpdatedAt);

public sealed record AdminSellerDto(
    Guid OrganizationId,
    string Name,
    string City,
    string Verification,
    int OfferCount,
    int PublishedCount,
    int BlockedOfferCount,
    bool Blocked,
    string? BlockReason,
    DateTimeOffset? BlockChangedAt,
    Guid? BlockChangedBy);

/// <summary>Optional reason (block) or note (unblock); stored in the audit log either way.</summary>
public sealed record ModerationRequest(string? Reason);

public sealed record BulkModerationRequest(Guid[]? OfferIds, string? Reason);

/// <param name="Changed">How many offers actually changed (already blocked/unblocked ones are left as they are).</param>
public sealed record BulkModerationResult(int Requested, int Changed, IReadOnlyList<AdminOfferDto> Offers);

public sealed record SellerModerationResult(AdminSellerDto Seller, bool Changed);

/// <summary>The seller's own view of a platform block of the whole organization.</summary>
public sealed record OwnModerationDto(bool SellerBlocked, string? Reason, DateTimeOffset? BlockedAt);

internal sealed class ModerationRequestValidator : AbstractValidator<ModerationRequest>
{
    public ModerationRequestValidator() => RuleFor(x => x.Reason).MaximumLength(OfferModeration.MaxReasonLength);
}

internal sealed class BulkModerationRequestValidator : AbstractValidator<BulkModerationRequest>
{
    public BulkModerationRequestValidator()
    {
        RuleFor(x => x.OfferIds).NotEmpty()
            .MaxCount(ids => ids!.Length, OfferModeration.MaxBatchSize, $"At most {OfferModeration.MaxBatchSize} offers per action.");
        RuleForEach(x => x.OfferIds).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(OfferModeration.MaxReasonLength);
    }
}

internal sealed class AdminOfferQueryValidator : AbstractValidator<AdminOfferQuery>
{
    public AdminOfferQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Brand).MaximumLength(100);
        RuleFor(x => x.Category).MaximumLength(100);
        RuleFor(x => x.Status).Must(s => string.IsNullOrWhiteSpace(s) || Enum.TryParse<OfferStatus>(s, true, out _))
            .WithMessage("Unknown offer status.").WithErrorCode("validation.offerStatus");
    }
}

/// <summary>
/// Platform-admin moderation of marketplace offers. Admin requests have no tenant; all reads and writes go through the
/// audited SECURITY DEFINER functions of migration MarketplaceModeration, which refuse to run for a tenant request.
/// Bulk actions update all named offers in one statement (one transaction), all or nothing.
/// </summary>
internal sealed class OfferModerationService(HandyNetDbContext db, ICurrentUser currentUser, ITenantContext tenant, IClock clock)
{
    public const int MaxSellers = 1000;

    public async Task<Result<PagedResult<AdminOfferDto>>> ListOffersAsync(AdminOfferQuery q, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var filter = new OfferFilter(
            null,
            q.SellerId,
            Blank(q.Brand),
            Blank(q.Category),
            Enum.TryParse<OfferStatus>(q.Status, true, out var parsed) ? parsed.ToString() : null,
            q.Blocked,
            string.IsNullOrWhiteSpace(q.Search) ? null : Text.LikePattern(q.Search),
            string.IsNullOrWhiteSpace(q.Search) ? null : Text.NormalizeKey(q.Search));

        var rows = await QueryOffersAsync(filter, (page - 1) * size, size, ct);
        // The total comes with every row (count(*) OVER ()); a page past the end needs one extra look.
        var total = rows.Count > 0 ? (int)rows[0].TotalCount
            : page > 1 ? (await QueryOffersAsync(filter, 0, 1, ct)).Select(r => (int)r.TotalCount).FirstOrDefault()
            : 0;
        return new PagedResult<AdminOfferDto>(rows.Select(ToDto).ToList(), page, size, total);
    }

    public async Task<Result<IReadOnlyList<AdminSellerDto>>> ListSellersAsync(string? search, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var pattern = string.IsNullOrWhiteSpace(search) ? null : Text.LikePattern(search);
        return (await QuerySellersAsync(null, pattern, ct)).Select(ToDto).ToList();
    }

    public async Task<Result<AdminOfferDto>> ModerateOfferAsync(Guid offerId, bool block, string? reason, CancellationToken ct)
    {
        var result = await ModerateOffersAsync([offerId], block, reason, ct);
        if (!result.IsSuccess)
            return result.Error!.Code == "moderation.offer_not_found"
                ? Error.NotFound("offer.not_found", "Offer not found.")
                : result.Error!;
        return result.Value.Offers[0];
    }

    /// <summary>Blocks or unblocks all named offers in one statement; if any id is unknown, nothing changes.</summary>
    public async Task<Result<BulkModerationResult>> ModerateOffersAsync(IEnumerable<Guid>? offerIds, bool block, string? reason, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var ids = OfferModeration.NormalizeBatch(offerIds);
        if (!ids.IsSuccess)
            return ids.Error!;
        var normalized = OfferModeration.NormalizeReason(reason);
        if (!normalized.IsSuccess)
            return normalized.Error!;

        var idArray = ids.Value.ToArray();
        var admin = currentUser.UserId;
        var now = clock.UtcNow;
        var reasonText = normalized.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Database.SqlQuery<ModeratedOfferRow>($"""
                SELECT offer_id, seller_organization_id, changed
                  FROM marketplace.moderate_offers({idArray}::uuid[], {block}::boolean, {reasonText}::text, {admin}::uuid, {now}::timestamptz)
                """)
            .ToListAsync(ct);

        var missing = idArray.Except(rows.Select(r => r.OfferId)).ToList();
        if (missing.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return Error.NotFound("moderation.offer_not_found", $"{missing.Count} of the selected offers do not exist. Nothing was changed.")
                .WithDetails(new Dictionary<string, object?> { ["offerIds"] = missing });
        }
        await tx.CommitAsync(ct);

        var offers = await QueryOffersAsync(new OfferFilter(idArray, null, null, null, null, null, null, null), 0, idArray.Length, ct);
        var byId = offers.ToDictionary(r => r.OfferId);
        return new BulkModerationResult(idArray.Length, rows.Count(r => r.Changed),
            idArray.Where(byId.ContainsKey).Select(id => ToDto(byId[id])).ToList());
    }

    /// <summary>Blocks or unblocks the whole seller: covers all its offers, including ones created while the block lasts.</summary>
    public async Task<Result<SellerModerationResult>> ModerateSellerAsync(Guid organizationId, bool block, string? reason, CancellationToken ct)
    {
        if (RequireNoTenant() is { } error)
            return error;
        var normalized = OfferModeration.NormalizeReason(reason);
        if (!normalized.IsSuccess)
            return normalized.Error!;

        var admin = currentUser.UserId;
        var now = clock.UtcNow;
        var reasonText = normalized.Value;
        var changed = await db.Database.SqlQuery<bool?>($"""
                SELECT marketplace.moderate_seller({organizationId}::uuid, {block}::boolean, {reasonText}::text, {admin}::uuid, {now}::timestamptz) AS "Value"
                """)
            .SingleAsync(ct);
        if (changed is null)
            return Error.NotFound("organization.not_found", "Organization not found.");

        // After a decision the organization always has a block row, so it is listed even without offers.
        var seller = (await QuerySellersAsync(organizationId, null, ct)).FirstOrDefault();
        return seller is null
            ? Error.NotFound("organization.not_found", "Organization not found.")
            : new SellerModerationResult(ToDto(seller), changed.Value);
    }

    private sealed record OfferFilter(
        Guid[]? Ids, Guid? Seller, string? Brand, string? Category, string? Status, bool? Blocked, string? Search, string? Key);

    private Task<List<AdminOfferRow>> QueryOffersAsync(OfferFilter f, int offset, int limit, CancellationToken ct) =>
        db.Database.SqlQuery<AdminOfferRow>($"""
                SELECT * FROM marketplace.admin_offers({f.Ids}::uuid[], {f.Seller}::uuid, {f.Brand}::text, {f.Category}::text,
                                                       {f.Status}::text, {f.Blocked}::boolean, {f.Search}::text, {f.Key}::text,
                                                       {offset}::integer, {limit}::integer)
                """)
            .ToListAsync(ct);

    private Task<List<AdminSellerRow>> QuerySellersAsync(Guid? organizationId, string? pattern, CancellationToken ct) =>
        db.Database.SqlQuery<AdminSellerRow>($"""
                SELECT * FROM marketplace.admin_sellers({organizationId}::uuid, {pattern}::text, {MaxSellers}::integer)
                """)
            .ToListAsync(ct);

    /// <summary>Platform actions run without a tenant; a tenant header on an admin call is a client error.</summary>
    private Error? RequireNoTenant() => tenant.OrganizationId is null
        ? null
        : Error.Validation("moderation.tenant_header", "Platform moderation is called without an organization header.");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AdminOfferDto ToDto(AdminOfferRow r)
    {
        var offerBlocked = r.Moderation == nameof(ModerationStatus.Blocked);
        return new AdminOfferDto(r.OfferId, r.SellerOrganizationId, r.SellerName, r.SellerCity, r.ProductId, r.Brand, r.ProductName,
            r.Category, r.Ean, r.Mpn, r.SupplierSku, r.PriceMinor, r.Currency, r.Status, r.Moderation, r.ModerationReason, r.ModeratedAt,
            r.ModeratedBy, r.SellerBlocked, r.SellerBlockReason, offerBlocked || r.SellerBlocked,
            offerBlocked ? "offer" : r.SellerBlocked ? "seller" : null, r.UpdatedAt);
    }

    private static AdminSellerDto ToDto(AdminSellerRow r) => new(
        r.OrganizationId, r.Name, r.City, r.Verification, (int)r.OfferCount, (int)r.PublishedCount, (int)r.BlockedOfferCount,
        r.IsBlocked, r.BlockReason, r.BlockChangedAt, r.BlockChangedBy);

    internal sealed record AdminOfferRow(
        Guid OfferId, Guid SellerOrganizationId, string SellerName, string SellerCity, Guid ProductId, string Brand, string ProductName,
        string Category, string? Ean, string? Mpn, string SupplierSku, long PriceMinor, string Currency, string Status, string Moderation,
        string? ModerationReason, DateTimeOffset? ModeratedAt, Guid? ModeratedBy, bool SellerBlocked, string? SellerBlockReason,
        DateTimeOffset UpdatedAt, long TotalCount);

    internal sealed record AdminSellerRow(
        Guid OrganizationId, string Name, string City, string Verification, long OfferCount, long PublishedCount, long BlockedOfferCount,
        bool IsBlocked, string? BlockReason, DateTimeOffset? BlockChangedAt, Guid? BlockChangedBy);

    internal sealed record ModeratedOfferRow(Guid OfferId, Guid SellerOrganizationId, bool Changed);
}
