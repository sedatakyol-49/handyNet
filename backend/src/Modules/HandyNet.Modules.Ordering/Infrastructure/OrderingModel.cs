using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ordering.Infrastructure;

public sealed class OrderingModel : IModuleModel
{
    public const string Schema = "ordering";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>("order_number_seq", Schema).StartsAt(1000);

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders", Schema, t =>
            {
                t.HasCheckConstraint("ck_orders_parties", "buyer_organization_id <> seller_organization_id");
                t.HasCheckConstraint("ck_orders_totals", "net_total_minor > 0 AND vat_total_minor >= 0 AND gross_total_minor = net_total_minor + vat_total_minor");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Number).HasDefaultValueSql("nextval('ordering.order_number_seq')").ValueGeneratedOnAdd();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.BuyerReference).HasMaxLength(100);
            b.Property(x => x.CloseReason).HasMaxLength(500);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.DisplayNumber);
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.BuyerOrganizationId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.BuyerOrganizationId, x.PlacedAt });
            b.HasIndex(x => new { x.SellerOrganizationId, x.Status, x.PlacedAt });
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.BuyerOrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.SellerOrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines", Schema, t =>
            {
                t.HasCheckConstraint("ck_order_lines_quantity", "quantity > 0");
                t.HasCheckConstraint("ck_order_lines_amounts", "unit_price_minor > 0 AND line_net_minor = unit_price_minor * quantity");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.SupplierSku).HasMaxLength(100).IsRequired();
            b.Property(x => x.Brand).HasMaxLength(100).IsRequired();
            b.Property(x => x.ProductName).HasMaxLength(300).IsRequired();
            b.HasIndex(x => new { x.OrderId, x.LineNumber }).IsUnique();
            b.HasIndex(x => x.ReservationId).IsUnique().HasFilter("reservation_id IS NOT NULL");
            b.HasOne<Offer>().WithMany().HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<StockReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
