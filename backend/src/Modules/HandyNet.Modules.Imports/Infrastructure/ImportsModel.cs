using HandyNet.Modules.Imports.Domain;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Imports.Infrastructure;

public sealed class ImportsModel : IModuleModel
{
    public const string Schema = "imports";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatch>(b =>
        {
            b.ToTable("import_batches", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            b.Property(x => x.FileSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.Format).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Headers).HasColumnType("text[]");
            b.Property(x => x.Mapping).HasJsonConversion();
            b.Property(x => x.Summary).HasJsonConversion();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            b.HasIndex(x => new { x.OrganizationId, x.FileSha256 });
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<ImportRow>(b =>
        {
            b.ToTable("import_rows", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.Raw).HasJsonConversion();
            b.Property(x => x.Messages).HasColumnType("text[]");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ProductAction).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.OfferAction).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Sku).HasMaxLength(100);
            b.Property(x => x.Ean).HasMaxLength(14);
            b.Property(x => x.Mpn).HasMaxLength(100);
            b.Property(x => x.Brand).HasMaxLength(100);
            b.Property(x => x.Name).HasMaxLength(300);
            b.Property(x => x.Category).HasMaxLength(30);
            b.Property(x => x.Quality).HasMaxLength(30);
            b.Property(x => x.Condition).HasMaxLength(20);
            b.Property(x => x.ModelName).HasMaxLength(200);
            b.HasIndex(x => new { x.BatchId, x.RowNumber }).IsUnique();
            b.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
