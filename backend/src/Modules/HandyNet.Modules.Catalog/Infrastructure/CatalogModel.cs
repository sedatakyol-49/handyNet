using HandyNet.Modules.Catalog.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Catalog.Infrastructure;

public sealed class CatalogModel : IModuleModel
{
    public const string Schema = "catalog";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products", Schema, t =>
                t.HasCheckConstraint("ck_products_identifier", "ean IS NOT NULL OR mpn_key IS NOT NULL"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Brand).HasMaxLength(100).IsRequired();
            b.Property(x => x.BrandKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.Name).HasMaxLength(300).IsRequired();
            b.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Quality).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Mpn).HasMaxLength(100);
            b.Property(x => x.MpnKey).HasMaxLength(100);
            b.Property(x => x.Ean).HasMaxLength(14).IsFixedLength(false);
            b.Property(x => x.ModelName).HasMaxLength(200);
            b.Property(x => x.CompatibleModels).HasColumnType("text[]");
            b.Property(x => x.ModelKeys).HasColumnType("text[]");
            b.Property(x => x.Version).IsRowVersion();

            b.HasIndex(x => x.Ean).IsUnique().HasFilter("ean IS NOT NULL");
            b.HasIndex(x => new { x.BrandKey, x.MpnKey, x.Quality }).IsUnique().HasFilter("mpn_key IS NOT NULL")
                .HasDatabaseName("ux_products_identity");
            b.HasIndex(x => x.Category);
            // Marketplace filters (brand list, quality, "passend für").
            b.HasIndex(x => x.Brand);
            b.HasIndex(x => x.Quality);
            b.HasIndex(x => x.ModelKeys).HasMethod("gin");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<ProductImage>(b =>
        {
            b.ToTable("product_images", Schema, t =>
            {
                t.HasCheckConstraint("ck_product_images_position", $"position IS NULL OR (position >= 0 AND position < {ProductImage.MaxPerProduct})");
                t.HasCheckConstraint("ck_product_images_removed", "(removed_at IS NULL) = (position IS NOT NULL)");
                t.HasCheckConstraint("ck_product_images_sizes", "size_bytes > 0 AND thumbnail_size_bytes > 0 AND original_size_bytes > 0 AND width > 0 AND height > 0 AND thumbnail_width > 0 AND thumbnail_height > 0");
                t.HasCheckConstraint("ck_product_images_content_type", "content_type IN ('image/webp', 'image/jpeg', 'image/png')");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.StorageKey).HasMaxLength(400).IsRequired();
            b.Property(x => x.ThumbnailStorageKey).HasMaxLength(400).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
            b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.OriginalFormat).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.IsActive);
            b.HasIndex(x => x.StorageKey).IsUnique();
            b.HasIndex(x => x.ProductId).HasFilter("removed_at IS NULL").HasDatabaseName("ix_product_images_active");
            // Unique (product_id, position) is a DEFERRABLE constraint added in the migration (reordering swaps positions).
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
