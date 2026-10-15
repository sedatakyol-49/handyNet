using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ledger.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum AccountType
{
    /// <summary>Funds held by the payment service provider on the marketplace's behalf (platform account).</summary>
    PspClearing,

    /// <summary>What the marketplace owes a seller for paid sales not yet paid out (one per seller).</summary>
    SellerPayable,

    /// <summary>Platform commission earned, net of VAT.</summary>
    CommissionRevenue,

    /// <summary>VAT on platform commission owed to the tax office.</summary>
    VatPayable,
}

/// <summary>
/// One side of a journal entry. Positive = debit, negative = credit. <see cref="OrganizationId"/> is null for
/// platform accounts and set for per-organization accounts such as <see cref="AccountType.SellerPayable"/>.
/// </summary>
public sealed record PostingLine(AccountType Account, Guid? OrganizationId, long AmountMinor, string Currency);

public sealed record JournalEntryDraft(
    string IdempotencyKey,
    string Description,
    string SourceType,
    Guid SourceId,
    DateTimeOffset OccurredAt,
    IReadOnlyList<PostingLine> Lines);

public static class JournalRules
{
    /// <summary>Validates a draft before it is sent to the database (which enforces the same rules again).</summary>
    public static Error? Validate(JournalEntryDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.IdempotencyKey) || draft.IdempotencyKey.Length > 200)
            return Error.Validation("ledger.idempotency_key", "Journal entries need an idempotency key (max 200 characters).");
        if (draft.Lines.Count < 2)
            return Error.Validation("ledger.too_few_lines", "A journal entry needs at least two postings.");
        if (draft.Lines.Any(l => l.AmountMinor == 0))
            return Error.Validation("ledger.zero_posting", "Postings must not be zero.");
        if (draft.Lines.Any(l => (l.Account == AccountType.SellerPayable) != (l.OrganizationId is not null)))
            return Error.Validation("ledger.account_owner", "Seller accounts need an organization; platform accounts must not have one.");
        var unbalanced = draft.Lines.GroupBy(l => l.Currency).FirstOrDefault(g => g.Sum(l => l.AmountMinor) != 0);
        if (unbalanced is not null)
            return Error.Validation("ledger.unbalanced", $"Debits and credits differ in {unbalanced.Key}.");
        return null;
    }

    /// <summary>The same entry with every amount negated — used for reversals (refunds).</summary>
    public static IReadOnlyList<PostingLine> Reverse(IEnumerable<PostingLine> lines) =>
        lines.Select(l => l with { AmountMinor = -l.AmountMinor }).ToList();
}
