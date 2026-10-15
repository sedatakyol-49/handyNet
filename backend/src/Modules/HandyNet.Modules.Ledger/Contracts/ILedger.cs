using HandyNet.Modules.Ledger.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ledger.Contracts;

public sealed record AccountBalance(AccountType Account, string Currency, long BalanceMinor);

public sealed record LedgerLineDto(Guid EntryId, DateTimeOffset OccurredAt, string Description, string SourceType, Guid SourceId,
    string Account, long AmountMinor, string Currency);

/// <summary>
/// Public API of the Ledger module. Entries are immutable; corrections are new (reversing) entries.
/// Posting goes through the database function <c>ledger.post_entry</c>, which re-checks balance and
/// idempotency, so even a buggy caller cannot write an unbalanced or duplicate entry.
/// </summary>
public interface ILedger
{
    /// <summary>Posts the entry in the current transaction. Returns the entry id; repeating a key returns the existing id.</summary>
    Task<Result<Guid>> PostAsync(JournalEntryDraft draft, CancellationToken ct);

    /// <summary>Balances of the current tenant's own accounts (credit balances shown as positive amounts).</summary>
    Task<IReadOnlyList<AccountBalance>> GetOwnBalancesAsync(CancellationToken ct);

    Task<PagedResult<LedgerLineDto>> GetOwnLinesAsync(int? page, int? pageSize, CancellationToken ct);
}
