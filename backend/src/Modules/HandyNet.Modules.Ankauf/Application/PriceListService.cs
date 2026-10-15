using System.Security.Cryptography;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Ankauf.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ankauf.Application;

public sealed record PriceListSummary(int Total, int New, int Unchanged, int Increases, int Decreases, int LargeDecreases,
    int Pending, int Approved, int Rejected, int AutoAccepted, int Skipped);

public sealed record PriceListDto(
    Guid Id,
    string Label,
    string FileName,
    string SheetName,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    string Status,
    DateTimeOffset ImportedAt,
    DateTimeOffset? PublishedAt,
    PriceListSummary Summary,
    IReadOnlyList<SkippedRow> SkippedRows);

public sealed record PriceListEntryDto(long Id, int RowNumber, string Brand, string Model, string? Storage, long PriceMinor,
    long? PreviousPriceMinor, string Change, string Decision, DateTimeOffset? DecidedAt);

public sealed record DecisionRequest(IReadOnlyList<long> EntryIds, bool Approve);

public sealed record PolicyDto(Guid Id, int Number, PolicySettings Settings, DateTimeOffset EffectiveFrom, DateTimeOffset CreatedAt, bool IsActive);

public sealed record CreatePolicyRequest(PolicySettings Settings, DateTimeOffset? EffectiveFrom);

/// <summary>Platform-admin workflow for monthly price lists and pricing policies (platform data, no tenant).</summary>
internal sealed class PriceListService(HandyNetDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<Result<PriceListDto>> UploadAsync(string fileName, byte[] content, string label, DateTimeOffset validFrom,
        DateTimeOffset validUntil, string? sheetName, CancellationToken ct)
    {
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Error.Validation("ankauf.format", "Upload the price list as .xlsx.");
        if (content.LongLength > PriceListReader.MaxBytes)
            return Error.Validation("ankauf.too_large", "Files are limited to 10 MB.");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100)
            return Error.Validation("ankauf.label", "Give the list a label, e.g. \"Oktober 2026\".");

        var sheet = PriceListReader.Read(content, sheetName);
        if (!sheet.IsSuccess)
            return sheet.Error!;
        if (sheet.Value.Rows.Count == 0)
            return Error.Validation("ankauf.no_rows", "The sheet contains no priced models.");

        var duplicates = sheet.Value.Rows.GroupBy(r => ModelKeys.Of(r.Brand, r.Model, r.Storage)).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
            return Error.Validation("ankauf.duplicate_models", "Models listed more than once: "
                + string.Join("; ", duplicates.Take(10).Select(g => $"{g.First().Brand} {g.First().Model} {g.First().Storage} (rows {string.Join(", ", g.Select(r => r.RowNumber))})")));

        var list = PriceList.Create(label, Path.GetFileName(fileName), Convert.ToHexStringLower(SHA256.HashData(content)), sheet.Value.SheetName,
            validFrom, validUntil, sheet.Value.Skipped, currentUser.UserId, clock.UtcNow);
        if (!list.IsSuccess)
            return list.Error!;

        var previous = await LatestPublishedPricesBeforeAsync(validFrom, ct);
        db.Add(list.Value);
        foreach (var row in sheet.Value.Rows)
        {
            var key = ModelKeys.Of(row.Brand, row.Model, row.Storage);
            db.Add(PriceListEntry.Create(list.Value.Id, row.RowNumber, row.Brand, row.Model, row.Storage, row.PriceMinor,
                previous.TryGetValue(key, out var p) ? p : null));
        }
        await db.SaveChangesAsync(ct);

        return await ToDtoAsync(list.Value, ct);
    }

    /// <summary>The most recent published price per model that started before the new list (expired or not) — the comparison basis.</summary>
    private async Task<Dictionary<string, long>> LatestPublishedPricesBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        var versions = await db.Set<PriceVersion>().AsNoTracking()
            .Where(v => v.ValidFrom < before)
            .Select(v => new { v.ModelKey, v.ValidFrom, v.PriceMinor })
            .ToListAsync(ct);
        return versions.GroupBy(v => v.ModelKey).ToDictionary(g => g.Key, g => g.MaxBy(v => v.ValidFrom)!.PriceMinor);
    }

    public async Task<IReadOnlyList<PriceListDto>> ListAsync(CancellationToken ct)
    {
        var lists = await db.Set<PriceList>().AsNoTracking().OrderByDescending(l => l.ImportedAt).Take(50).ToListAsync(ct);
        var result = new List<PriceListDto>();
        foreach (var list in lists)
            result.Add(await ToDtoAsync(list, ct));
        return result;
    }

    public async Task<Result<PriceListDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<PriceList>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct) is { } list
            ? await ToDtoAsync(list, ct)
            : Error.NotFound("ankauf.list_not_found", "Price list not found.");

    public async Task<Result<PagedResult<PriceListEntryDto>>> GetEntriesAsync(Guid id, string? decision, string? change, string? search,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (!await db.Set<PriceList>().AnyAsync(l => l.Id == id, ct))
            return Error.NotFound("ankauf.list_not_found", "Price list not found.");
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<PriceListEntry>().AsNoTracking().Where(e => e.PriceListId == id);
        if (Enum.TryParse<EntryDecision>(decision, true, out var d))
            query = query.Where(e => e.Decision == d);
        if (Enum.TryParse<PriceChange>(change, true, out var c))
            query = query.Where(e => e.Change == c);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Text.LikePattern(search);
            query = query.Where(e => EF.Functions.ILike(e.Brand + " " + e.Model, pattern));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(e => e.RowNumber).Skip((p - 1) * size).Take(size)
            .Select(e => new PriceListEntryDto(e.Id, e.RowNumber, e.Brand, e.Model, e.Storage, e.PriceMinor, e.PreviousPriceMinor,
                e.Change.ToString(), e.Decision.ToString(), e.DecidedAt))
            .ToListAsync(ct);
        return new PagedResult<PriceListEntryDto>(items, p, size, total);
    }

    public async Task<Result<PriceListDto>> DecideAsync(Guid id, DecisionRequest request, CancellationToken ct)
    {
        var list = await db.Set<PriceList>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null)
            return Error.NotFound("ankauf.list_not_found", "Price list not found.");
        if (list.Status != PriceListStatus.Review)
            return Error.Conflict("ankauf.list_not_in_review", "Decisions are only possible while the list is in review.");
        if (request.EntryIds.Count is 0 or > 1000)
            return Error.Validation("ankauf.decision_count", "Select between 1 and 1000 entries.");

        var entries = await db.Set<PriceListEntry>().Where(e => e.PriceListId == id && request.EntryIds.Contains(e.Id)).ToListAsync(ct);
        if (entries.Count != request.EntryIds.Distinct().Count())
            return Error.NotFound("ankauf.entry_not_found", "Some entries do not belong to this list.");
        foreach (var entry in entries)
        {
            var decided = entry.Decide(request.Approve, currentUser.UserId, clock.UtcNow);
            if (!decided.IsSuccess)
                return decided.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(list, ct);
    }

    /// <summary>Turns accepted and approved entries into immutable price versions. Requires every entry to be decided.</summary>
    public async Task<Result<PriceListDto>> PublishAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var list = await db.Set<PriceList>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null)
            return Error.NotFound("ankauf.list_not_found", "Price list not found.");
        var pending = await db.Set<PriceListEntry>().CountAsync(e => e.PriceListId == id && e.Decision == EntryDecision.Pending, ct);
        if (pending > 0)
            return Error.Conflict("ankauf.pending_decisions", $"{pending} prices still need an approval decision.");

        var published = list.Publish(currentUser.UserId, clock.UtcNow);
        if (!published.IsSuccess)
            return published.Error!;

        var accepted = await db.Set<PriceListEntry>().AsNoTracking()
            .Where(e => e.PriceListId == id && (e.Decision == EntryDecision.AutoAccepted || e.Decision == EntryDecision.Approved))
            .ToListAsync(ct);
        db.AddRange(accepted.Select(e => PriceVersion.From(e, list, clock.UtcNow)));

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The list was changed concurrently. Reload and try again.");
        }
        return await ToDtoAsync(list, ct);
    }

    public async Task<Result<PriceListDto>> DiscardAsync(Guid id, CancellationToken ct)
    {
        var list = await db.Set<PriceList>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null)
            return Error.NotFound("ankauf.list_not_found", "Price list not found.");
        var discarded = list.Discard();
        if (!discarded.IsSuccess)
            return discarded.Error!;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(list, ct);
    }

    // ---- policies ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PolicyDto>> ListPoliciesAsync(CancellationToken ct)
    {
        var policies = await db.Set<ConditionPolicy>().AsNoTracking().OrderByDescending(p => p.Number).ToListAsync(ct);
        var activeId = ActivePolicy(policies, clock.UtcNow)?.Id;
        return policies.Select(p => new PolicyDto(p.Id, p.Number, p.Settings, p.EffectiveFrom, p.CreatedAt, p.Id == activeId)).ToList();
    }

    public async Task<Result<PolicyDto>> CreatePolicyAsync(CreatePolicyRequest request, CancellationToken ct)
    {
        var number = (await db.Set<ConditionPolicy>().MaxAsync(p => (int?)p.Number, ct) ?? 0) + 1;
        var policy = ConditionPolicy.Create(number, request.Settings, request.EffectiveFrom ?? clock.UtcNow, currentUser.UserId, clock.UtcNow);
        if (!policy.IsSuccess)
            return policy.Error!;
        db.Add(policy.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("concurrency", "Another policy was created at the same time. Reload and try again.");
        }
        var p = policy.Value;
        return new PolicyDto(p.Id, p.Number, p.Settings, p.EffectiveFrom, p.CreatedAt, p.EffectiveFrom <= clock.UtcNow);
    }

    internal static ConditionPolicy? ActivePolicy(IEnumerable<ConditionPolicy> policies, DateTimeOffset now) =>
        policies.Where(p => p.EffectiveFrom <= now).MaxBy(p => p.EffectiveFrom);

    private async Task<PriceListDto> ToDtoAsync(PriceList list, CancellationToken ct)
    {
        var counts = await db.Set<PriceListEntry>().AsNoTracking().Where(e => e.PriceListId == list.Id)
            .GroupBy(e => new { e.Change, e.Decision })
            .Select(g => new { g.Key.Change, g.Key.Decision, Count = g.Count() })
            .ToListAsync(ct);
        int ByChange(PriceChange c) => counts.Where(x => x.Change == c).Sum(x => x.Count);
        int ByDecision(EntryDecision d) => counts.Where(x => x.Decision == d).Sum(x => x.Count);

        var skipped = list.SkippedRows;

        var summary = new PriceListSummary(counts.Sum(x => x.Count), ByChange(PriceChange.New), ByChange(PriceChange.Unchanged),
            ByChange(PriceChange.Increase), ByChange(PriceChange.Decrease), ByChange(PriceChange.LargeDecrease),
            ByDecision(EntryDecision.Pending), ByDecision(EntryDecision.Approved), ByDecision(EntryDecision.Rejected),
            ByDecision(EntryDecision.AutoAccepted), skipped.Count);
        return new PriceListDto(list.Id, list.Label, list.FileName, list.SheetName, list.ValidFrom, list.ValidUntil, list.Status.ToString(),
            list.ImportedAt, list.PublishedAt, summary, skipped);
    }
}
