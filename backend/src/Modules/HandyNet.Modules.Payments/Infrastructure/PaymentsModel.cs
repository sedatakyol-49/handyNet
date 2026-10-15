using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Payments.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Payments.Infrastructure;

public sealed class PaymentsModel : IModuleModel
{
    public const string Schema = "payments";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments", Schema, t =>
            {
                t.HasCheckConstraint("ck_payments_amounts",
                    "amount_minor > 0 AND commission_net_minor >= 0 AND commission_vat_minor >= 0 AND commission_net_minor + commission_vat_minor <= amount_minor");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ProviderPaymentId).HasMaxLength(100).IsRequired();
            b.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.CheckoutUrl).HasMaxLength(1000);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.Split);
            b.Ignore(x => x.DomainEvents);
            b.HasIndex(x => new { x.Provider, x.ProviderPaymentId }).IsUnique();
            // At most one live (open or paid) payment per order — a second checkout cannot charge the buyer twice.
            b.HasIndex(x => x.OrderId).IsUnique().HasFilter("status IN ('Open', 'Paid')").HasDatabaseName("ux_payments_one_live_per_order");
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payout>(b =>
        {
            b.ToTable("payouts", Schema, t => t.HasCheckConstraint("ck_payouts_amount", "amount_minor > 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.ProviderTransferId).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.PaymentId).IsUnique();
            b.HasIndex(x => new { x.SellerOrganizationId, x.CreatedAt });
            b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
