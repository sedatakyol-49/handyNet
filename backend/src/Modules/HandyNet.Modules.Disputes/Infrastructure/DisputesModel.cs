using HandyNet.Modules.Disputes.Domain;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Disputes.Infrastructure;

public sealed class DisputesModel : IModuleModel
{
    public const string Schema = "disputes";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Dispute>(b =>
        {
            b.ToTable("disputes", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired();
            b.Property(x => x.Reason).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ResolvedBy).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Resolution).HasMaxLength(Dispute.MaxTextLength);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.IsActive);
            b.Ignore(x => x.DomainEvents);
            // One active dispute per order.
            b.HasIndex(x => x.OrderId).IsUnique().HasFilter("status IN ('Open', 'Escalated')").HasDatabaseName("ux_disputes_one_active_per_order");
            b.HasIndex(x => x.Status);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.DisputeId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<DisputeMessage>(b =>
        {
            b.ToTable("dispute_messages", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever(); // added to a tracked dispute; see ShippingModel
            b.Property(x => x.From).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Text).HasMaxLength(Dispute.MaxTextLength).IsRequired();
            b.HasIndex(x => new { x.DisputeId, x.SentAt });
        });
    }
}
