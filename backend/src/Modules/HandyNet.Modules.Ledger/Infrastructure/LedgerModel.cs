using HandyNet.Modules.Ledger.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ledger.Infrastructure;

/// <summary>Read-side mapping. Rows are only ever written by the SQL function <c>ledger.post_entry</c>.</summary>
public sealed class LedgerAccount
{
    public Guid Id { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public AccountType Type { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class JournalEntry
{
    public Guid Id { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public string SourceType { get; private set; } = null!;
    public Guid SourceId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}

public sealed class Posting
{
    public long Id { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public Guid AccountId { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;
}

public sealed class LedgerModel : IModuleModel
{
    public const string Schema = "ledger";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerAccount>(b =>
        {
            b.ToTable("accounts", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.HasIndex(x => new { x.OrganizationId, x.Type, x.Currency }).IsUnique().AreNullsDistinct(false);
        });

        modelBuilder.Entity<JournalEntry>(b =>
        {
            b.ToTable("journal_entries", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.SourceType).HasMaxLength(50).IsRequired();
            b.HasIndex(x => x.IdempotencyKey).IsUnique();
            b.HasIndex(x => new { x.SourceType, x.SourceId });
        });

        modelBuilder.Entity<Posting>(b =>
        {
            b.ToTable("postings", Schema, t => t.HasCheckConstraint("ck_postings_nonzero", "amount_minor <> 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.HasIndex(x => x.JournalEntryId);
            b.HasIndex(x => x.AccountId);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
