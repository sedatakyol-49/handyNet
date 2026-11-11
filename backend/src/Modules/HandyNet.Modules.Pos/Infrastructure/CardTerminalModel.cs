using HandyNet.Modules.Pos.Domain;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>EF mapping of <c>pos.terminal_profiles</c> and <c>pos.card_transactions</c> (ADR-0021); RLS, grants and the guard trigger are in the migration.</summary>
internal static class CardTerminalModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TerminalProfile>(b =>
        {
            b.ToTable("terminal_profiles", PosModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_terminal_profiles_connection", "connection IN ('Manual', 'Simulated', 'ZvtBridge')");
                t.HasCheckConstraint("ck_terminal_profiles_port", "port IS NULL OR port BETWEEN 1 AND 65535");
                t.HasCheckConstraint("ck_terminal_profiles_zvt",
                    "(connection = 'ZvtBridge' AND host IS NOT NULL AND port IS NOT NULL AND bridge_url IS NOT NULL AND zvt_password ~ '^[0-9]{6}$') "
                    + "OR (connection <> 'ZvtBridge' AND host IS NULL AND port IS NULL AND bridge_url IS NULL AND zvt_password IS NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(TerminalProfile.MaxNameLength).IsRequired();
            b.Property(x => x.Connection).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Host).HasMaxLength(PrinterProfile.MaxHostLength);
            b.Property(x => x.BridgeUrl).HasMaxLength(PrinterProfile.MaxBridgeUrlLength);
            b.Property(x => x.ZvtPassword).HasMaxLength(6);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.RegisterId, x.Name }).IsUnique();
            // At most one default profile per register.
            b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_terminal_profiles_one_default");
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId });
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CardTransaction>(b =>
        {
            b.ToTable("card_transactions", PosModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_card_transactions_kind", "kind IN ('Payment', 'Refund')");
                t.HasCheckConstraint("ck_card_transactions_amount", "amount_minor > 0");
                t.HasCheckConstraint("ck_card_transactions_status", "status IN ('Pending', 'Approved', 'Declined', 'Aborted', 'Failed', 'Reversed')");
                t.HasCheckConstraint("ck_card_transactions_completed", "(status = 'Pending') = (completed_at IS NULL)");
                // Only an approved transaction settles a receipt; a reversed one never did.
                t.HasCheckConstraint("ck_card_transactions_link", "(receipt_id IS NULL AND linked_at IS NULL) OR (receipt_id IS NOT NULL AND linked_at IS NOT NULL AND status = 'Approved')");
                t.HasCheckConstraint("ck_card_transactions_reversal", "(status = 'Reversed') = (reversed_at IS NOT NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TerminalName).HasMaxLength(TerminalProfile.MaxNameLength);
            b.Property(x => x.TerminalId).HasMaxLength(CardTransaction.MaxShortLength);
            b.Property(x => x.TraceNumber).HasMaxLength(CardTransaction.MaxShortLength);
            b.Property(x => x.TerminalReceiptNumber).HasMaxLength(CardTransaction.MaxShortLength);
            b.Property(x => x.AuthCode).HasMaxLength(CardTransaction.MaxShortLength);
            b.Property(x => x.ResultCode).HasMaxLength(CardTransaction.MaxShortLength);
            b.Property(x => x.CardName).HasMaxLength(CardTransaction.MaxCardNameLength);
            b.Property(x => x.MaskedPan).HasMaxLength(CardTransaction.MaxPanLength);
            b.Property(x => x.ResultText).HasMaxLength(CardTransaction.MaxResultTextLength);
            b.Property(x => x.ReversalText).HasMaxLength(CardTransaction.MaxResultTextLength);
            b.Property(x => x.CustomerReceiptText).HasMaxLength(CardTransaction.MaxReceiptTextLength);
            b.Property(x => x.MerchantReceiptText).HasMaxLength(CardTransaction.MaxReceiptTextLength);
            b.Property(x => x.Version).IsRowVersion();
            // A receipt has one card payment row, so at most one card transaction settles it.
            b.HasIndex(x => x.ReceiptId).IsUnique().HasFilter("receipt_id IS NOT NULL").HasDatabaseName("ux_card_transactions_receipt");
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId, x.CreatedAt });
            b.HasIndex(x => new { x.OrganizationId, x.Status }).HasFilter("receipt_id IS NULL");
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
