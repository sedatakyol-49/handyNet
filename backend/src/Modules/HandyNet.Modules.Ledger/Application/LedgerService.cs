using System.Text.Json;
using HandyNet.Modules.Ledger.Contracts;
using HandyNet.Modules.Ledger.Domain;
using HandyNet.Modules.Ledger.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ledger.Application;

internal sealed class LedgerService(HandyNetDbContext db, ITenantContext tenant) : ILedger
{
    public async Task<Result<Guid>> PostAsync(JournalEntryDraft draft, CancellationToken ct)
    {
        if (JournalRules.Validate(draft) is { } invalid)
            return invalid;

        var lines = JsonSerializer.Serialize(draft.Lines.Select(l => new
        {
            account_type = l.Account.ToString(),
            organization_id = l.OrganizationId,
            amount_minor = l.AmountMinor,
            currency = l.Currency,
        }));

        var id = await db.Database
            .SqlQuery<Guid>($"""
                SELECT ledger.post_entry({draft.IdempotencyKey}, {draft.Description}, {draft.SourceType}, {draft.SourceId},
                                         {draft.OccurredAt}, {lines}::jsonb) AS "Value"
                """)
            .SingleAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<AccountBalance>> GetOwnBalancesAsync(CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        var rows = await (
                from a in db.Set<LedgerAccount>().AsNoTracking()
                where a.OrganizationId == organizationId
                join p in db.Set<Posting>().AsNoTracking() on a.Id equals p.AccountId into postings
                select new { a.Type, a.Currency, Sum = postings.Sum(p => (long?)p.AmountMinor) ?? 0 })
            .ToListAsync(ct);
        // Liability accounts carry credit (negative) balances; show what is owed as a positive number.
        return rows.Select(r => new AccountBalance(r.Type, r.Currency, -r.Sum)).ToList();
    }

    public async Task<PagedResult<LedgerLineDto>> GetOwnLinesAsync(int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var organizationId = tenant.RequiredOrganizationId;
        var query =
            from posting in db.Set<Posting>().AsNoTracking()
            join account in db.Set<LedgerAccount>().AsNoTracking() on posting.AccountId equals account.Id
            join entry in db.Set<JournalEntry>().AsNoTracking() on posting.JournalEntryId equals entry.Id
            where account.OrganizationId == organizationId
            select new { posting, account, entry };

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.entry.OccurredAt).ThenByDescending(x => x.posting.Id)
            .Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<LedgerLineDto>(rows.Select(x => new LedgerLineDto(x.entry.Id, x.entry.OccurredAt, x.entry.Description,
            x.entry.SourceType, x.entry.SourceId, x.account.Type.ToString(), x.posting.AmountMinor, x.posting.Currency)).ToList(), p, size, total);
    }
}
