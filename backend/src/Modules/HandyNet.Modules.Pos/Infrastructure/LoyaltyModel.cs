using HandyNet.Modules.Pos.Domain;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>EF mapping of the loyalty tables (ADR-0020); RLS, grants and triggers are in the migration.</summary>
internal static class LoyaltyModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoyaltySettings>(b =>
        {
            b.ToTable("loyalty_settings", PosModel.Schema, t => t.HasCheckConstraint("ck_loyalty_settings_values",
                $"points_per_euro BETWEEN 0 AND {LoyaltySettings.MaxPointsPerEuro} AND point_value_minor BETWEEN 1 AND {LoyaltySettings.MaxPointValueMinor} "
                + $"AND min_redeem_points BETWEEN 0 AND {LoyaltySettings.MaxMinRedeemPoints}"));
            b.HasKey(x => x.OrganizationId);
            b.Property(x => x.OrganizationId).ValueGeneratedNever();
            b.Property(x => x.Version).IsRowVersion();
        });

        modelBuilder.Entity<LoyaltyAccount>(b =>
        {
            // Points can never go below zero.
            b.ToTable("loyalty_accounts", PosModel.Schema, t => t.HasCheckConstraint("ck_loyalty_accounts_points", "points >= 0"));
            b.HasKey(x => new { x.OrganizationId, x.CustomerId });
            b.Property(x => x.Version).IsRowVersion();
            b.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LoyaltyTransaction>(b =>
        {
            b.ToTable("loyalty_transactions", PosModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_loyalty_transactions_type", "type IN ('Earn', 'Redeem', 'ReverseEarn', 'ReverseRedeem', 'Adjust')");
                t.HasCheckConstraint("ck_loyalty_transactions_points",
                    "balance_after >= 0 AND points <> 0 AND ((type IN ('Earn', 'ReverseRedeem') AND points > 0) OR (type IN ('Redeem', 'ReverseEarn') AND points < 0) "
                    + "OR (type = 'Adjust' AND reason IS NOT NULL))");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reason).HasMaxLength(LoyaltyAccount.MaxReasonLength);
            b.HasIndex(x => new { x.OrganizationId, x.CustomerId, x.CreatedAt });
            b.HasIndex(x => x.ReceiptId).HasFilter("receipt_id IS NOT NULL");
            b.HasOne<LoyaltyAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
