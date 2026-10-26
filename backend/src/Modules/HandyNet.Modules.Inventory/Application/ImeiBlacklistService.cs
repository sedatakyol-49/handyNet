using FluentValidation;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Inventory.Application;

public sealed record AddBlacklistEntryRequest(string Imei, BlacklistReason Reason, string? Note, string Source);

public sealed record DeactivateBlacklistEntryRequest(string Reason);

public sealed record BlacklistEntryDto(
    Guid Id,
    string Imei,
    string Reason,
    string? Note,
    string Source,
    bool IsActive,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeactivatedAt,
    Guid? DeactivatedByUserId,
    string? DeactivationReason);

internal sealed class AddBlacklistEntryValidator : AbstractValidator<AddBlacklistEntryRequest>
{
    public AddBlacklistEntryValidator()
    {
        RuleFor(x => x.Imei).NotEmpty().Must(i => Imei.TryNormalize(i, out _)).WithMessage("IMEI must be 15 digits with a valid check digit.").WithErrorCode("validation.imei");
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Source).NotEmpty().MaximumLength(ImeiBlacklistEntry.MaxSourceLength);
        RuleFor(x => x.Note).MaximumLength(ImeiBlacklistEntry.MaxNoteLength);
    }
}

internal sealed class DeactivateBlacklistEntryValidator : AbstractValidator<DeactivateBlacklistEntryRequest>
{
    public DeactivateBlacklistEntryValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(ImeiBlacklistEntry.MaxDeactivationReasonLength);
}

/// <summary>
/// Platform administration of the IMEI blacklist. Runs in platform-admin sessions only (endpoint policy + RLS on
/// <c>inventory.imei_blacklist</c> via <c>app.platform_admin</c>). Entries are never deleted.
/// </summary>
internal sealed class ImeiBlacklistService(HandyNetDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<Result<PagedResult<BlacklistEntryDto>>> ListAsync(string? imei, bool? active, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<ImeiBlacklistEntry>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(imei))
        {
            var prefix = imei.Trim();
            if (prefix.Length > 15 || !prefix.All(char.IsAsciiDigit))
                return Error.Validation("blacklist.search", "Search by the first digits of the IMEI (digits only, max 15).");
            query = query.Where(e => e.Imei.StartsWith(prefix));
        }
        if (active is true)
            query = query.Where(e => e.DeactivatedAt == null);
        else if (active is false)
            query = query.Where(e => e.DeactivatedAt != null);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<BlacklistEntryDto>(items.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<Result<BlacklistEntryDto>> AddAsync(AddBlacklistEntryRequest request, CancellationToken ct)
    {
        var entry = ImeiBlacklistEntry.Create(request.Imei, request.Reason, request.Note, request.Source, currentUser.UserId, clock.UtcNow);
        if (!entry.IsSuccess)
            return entry.Error!;
        if (await db.Set<ImeiBlacklistEntry>().AnyAsync(e => e.Imei == entry.Value.Imei && e.DeactivatedAt == null, ct))
            return AlreadyActive(entry.Value.Imei);

        db.Add(entry.Value);
        AdminAudit.Action(db, clock, currentUser.UserId, nameof(ImeiBlacklistEntry), entry.Value.Id.ToString(), null, "ImeiBlock",
            entry.Value.Note, after: new { entry.Value.Imei, Reason = entry.Value.Reason.ToString(), entry.Value.Source });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_imei_blacklist_active"))
        {
            return AlreadyActive(entry.Value.Imei);
        }
        return ToDto(entry.Value);
    }

    public async Task<Result<BlacklistEntryDto>> DeactivateAsync(Guid id, DeactivateBlacklistEntryRequest request, CancellationToken ct)
    {
        var entry = await db.Set<ImeiBlacklistEntry>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return Error.NotFound("blacklist.not_found", "Blacklist entry not found.");
        var result = entry.Deactivate(request.Reason, currentUser.UserId, clock.UtcNow);
        if (!result.IsSuccess)
            return result.Error!;
        AdminAudit.Action(db, clock, currentUser.UserId, nameof(ImeiBlacklistEntry), entry.Id.ToString(), null, "ImeiUnblock",
            entry.DeactivationReason, details: new { entry.Imei });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The entry was changed concurrently. Reload and try again.");
        }
        return ToDto(entry);
    }

    private static Error AlreadyActive(string imei) =>
        Error.Conflict("blacklist.already_active", $"IMEI {imei} is already blacklisted.")
            .WithDetails(new Dictionary<string, object?> { ["imei"] = imei });

    private static BlacklistEntryDto ToDto(ImeiBlacklistEntry e) =>
        new(e.Id, e.Imei, e.Reason.ToString(), e.Note, e.Source, e.IsActive, e.CreatedByUserId, e.CreatedAt, e.DeactivatedAt,
            e.DeactivatedByUserId, e.DeactivationReason);
}
