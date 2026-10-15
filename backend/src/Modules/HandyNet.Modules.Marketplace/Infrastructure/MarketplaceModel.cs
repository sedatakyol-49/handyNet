using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Marketplace.Infrastructure;

public sealed class MarketplaceModel : IModuleModel
{
    public const string Schema = "marketplace";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Offer>(b =>
        {
            b.ToTable("offers", Schema, t =>
            {
                t.HasCheckConstraint("ck_offers_price", "price_minor > 0");
                t.HasCheckConstraint("ck_offers_min_quantity", "min_order_quantity >= 1");
                t.HasCheckConstraint("ck_offers_delivery_days", $"delivery_days BETWEEN 0 AND {Offer.MaxDeliveryDays}");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.SupplierSku).HasMaxLength(100).IsRequired();
            b.Property(x => x.SkuKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Condition).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.OrganizationId, x.SkuKey }).IsUnique();
            b.HasIndex(x => new { x.ProductId, x.Status });
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<OfferListing>(b =>
        {
            b.HasNoKey();
            b.ToView("offer_listings", Schema);
        });
    }
}
