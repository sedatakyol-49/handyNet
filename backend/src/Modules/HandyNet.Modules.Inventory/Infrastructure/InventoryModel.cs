using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Inventory.Infrastructure;

public sealed class InventoryModel : IModuleModel
{
    public const string Schema = "inventory";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Warehouse>(b =>
        {
            b.ToTable("warehouses", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(30).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
            b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_warehouses_one_default");
            // Composite alternate key so stock rows can reference (organization, warehouse) together:
            // a stock level can never point at another tenant's warehouse.
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
        });

        modelBuilder.Entity<StockLevel>(b =>
        {
            b.ToTable("stock_levels", Schema, t =>
            {
                t.HasCheckConstraint("ck_stock_levels_on_hand", "on_hand >= 0");
                t.HasCheckConstraint("ck_stock_levels_reserved", "reserved >= 0 AND reserved <= on_hand");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.Available);
            b.HasIndex(x => new { x.WarehouseId, x.ProductId }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.ProductId });
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockMovement>(b =>
        {
            b.ToTable("stock_movements", Schema, t => t.HasCheckConstraint("ck_stock_movements_nonzero", "quantity <> 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Reference).HasMaxLength(200);
            b.HasIndex(x => new { x.OrganizationId, x.ProductId, x.OccurredAt });
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockUnit>(b =>
        {
            b.ToTable("stock_units", Schema, t => t.HasCheckConstraint("ck_stock_units_purchase_price", "purchase_price_minor >= 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Imei).HasMaxLength(15).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TaxScheme).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Source).HasMaxLength(200).IsRequired();
            b.Property(x => x.Version).IsRowVersion();
            // One physical device can be in stock only once on the whole platform.
            b.HasIndex(x => x.Imei).IsUnique().HasFilter("status IN ('InStock', 'Reserved')").HasDatabaseName("ux_stock_units_imei_in_stock");
            b.HasIndex(x => new { x.OrganizationId, x.ProductId, x.Status });
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockReservation>(b =>
        {
            b.ToTable("stock_reservations", Schema, t => t.HasCheckConstraint("ck_stock_reservations_quantity", "quantity > 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reference).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.OrganizationId, x.Status });
            b.HasIndex(x => x.Reference);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
