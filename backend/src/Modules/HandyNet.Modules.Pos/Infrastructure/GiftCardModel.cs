using HandyNet.Modules.Pos.Domain;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>EF mapping of <c>pos.gift_cards</c> and <c>pos.gift_card_transactions</c> (ADR-0020); RLS, grants and triggers are in the migration.</summary>
internal static class GiftCardModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GiftCard>(b =>
        {
            b.ToTable("gift_cards", PosModel.Schema, t =>
            {
                // The balance can never be negative or exceed what the card was issued with.
                t.HasCheckConstraint("ck_gift_cards_balance", "initial_minor > 0 AND balance_minor >= 0 AND balance_minor <= initial_minor");
                t.HasCheckConstraint("ck_gift_cards_status", "status IN ('Active', 'Voided') AND (status = 'Active' OR balance_minor = 0)");
                t.HasCheckConstraint("ck_gift_cards_code", "code_hash ~ '^[0-9a-f]{64}$' AND char_length(code_last4) = 4");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.CodeHash).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.CodeLast4).HasMaxLength(4).IsFixedLength();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            b.HasIndex(x => new { x.OrganizationId, x.CodeHash }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.CodeLast4 });
            b.HasIndex(x => new { x.OrganizationId, x.IssuedAt });
            b.HasIndex(x => x.IssuedReceiptId);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.IssuedReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GiftCardTransaction>(b =>
        {
            b.ToTable("gift_card_transactions", PosModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_gift_card_transactions_type", "type IN ('Issue', 'Redeem', 'Restore', 'Void', 'Reissue')");
                t.HasCheckConstraint("ck_gift_card_transactions_amount",
                    "balance_after_minor >= 0 AND ((type IN ('Issue', 'Restore') AND amount_minor > 0) OR (type = 'Redeem' AND amount_minor < 0) "
                    + "OR (type = 'Void' AND amount_minor <= 0 AND balance_after_minor = 0) OR (type = 'Reissue' AND amount_minor = 0))");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reason).HasMaxLength(GiftCard.MaxReasonLength);
            b.HasIndex(x => new { x.GiftCardId, x.CreatedAt });
            b.HasIndex(x => x.ReceiptId).HasFilter("receipt_id IS NOT NULL");
            b.HasOne<GiftCard>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.GiftCardId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        // A gift card line names the card it sold.
        modelBuilder.Entity<ReceiptLine>(b =>
        {
            b.HasIndex(x => x.GiftCardId).HasFilter("gift_card_id IS NOT NULL");
            b.HasOne<GiftCard>().WithMany().HasForeignKey(x => x.GiftCardId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
