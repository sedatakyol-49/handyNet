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
            b.Property(x => x.Version).IsRowVersion();

            b.HasIndex(x => x.Ean).IsUnique().HasFilter("ean IS NOT NULL");
            b.HasIndex(x => new { x.BrandKey, x.MpnKey, x.Quality }).IsUnique().HasFilter("mpn_key IS NOT NULL")
                .HasDatabaseName("ux_products_identity");
            b.HasIndex(x => x.Category);
            b.Ignore(x => x.DomainEvents);
        });
    }
}
