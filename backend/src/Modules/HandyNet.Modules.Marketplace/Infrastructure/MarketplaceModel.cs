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
                // A block always records when/by whom; only a blocked offer carries a reason.
                t.HasCheckConstraint("ck_offers_moderation",
                    "(moderation = 'None' AND moderation_reason IS NULL) " +
                    "OR (moderation = 'Blocked' AND moderated_at IS NOT NULL AND moderated_by IS NOT NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.SupplierSku).HasMaxLength(100).IsRequired();
            b.Property(x => x.SkuKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Condition).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Moderation).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ModerationStatus.None).HasSentinel(ModerationStatus.None);
            b.Property(x => x.ModerationReason).HasMaxLength(OfferModeration.MaxReasonLength);
            b.Ignore(x => x.IsBlocked);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.OrganizationId, x.SkuKey }).IsUnique();
            b.HasIndex(x => new { x.ProductId, x.Status });
            // Marketplace browsing: published offers by price (VK range, default sort) and by delivery time.
            b.HasIndex(x => new { x.Status, x.PriceMinor });
            b.HasIndex(x => new { x.Status, x.DeliveryDays });
            // Moderation: the few blocked offers (admin filter "blocked").
            b.HasIndex(x => x.OrganizationId).HasFilter("moderation = 'Blocked'").HasDatabaseName("ix_offers_blocked");
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.DomainEvents);
        });

        // Platform-owned moderation of a whole seller. The app role may only read its own row (RLS); writes go through
        // the audited SECURITY DEFINER function marketplace.moderate_seller.
        modelBuilder.Entity<SellerBlock>(b =>
        {
            b.ToTable("seller_blocks", Schema);
            b.HasKey(x => x.OrganizationId);
            b.Property(x => x.OrganizationId).ValueGeneratedNever();
            b.Property(x => x.Reason).HasMaxLength(OfferModeration.MaxReasonLength);
            b.ToTable(t => t.HasCheckConstraint("ck_seller_blocks_reason", "is_blocked OR reason IS NULL"));
        });

        modelBuilder.Entity<OfferListing>(b =>
        {
            b.HasNoKey();
            b.ToView("offer_listings", Schema);
            b.Property(x => x.CompatibleModels).HasColumnType("text[]");
            b.Property(x => x.ModelKeys).HasColumnType("text[]");
        });
    }
}
