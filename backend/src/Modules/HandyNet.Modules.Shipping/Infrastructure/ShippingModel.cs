using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Shipping.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Shipping.Infrastructure;

public sealed class ShippingModel : IModuleModel
{
    public const string Schema = "shipping";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>(b =>
        {
            b.ToTable("shipments", Schema, t =>
            {
                t.HasCheckConstraint("ck_shipments_parcel", "weight_grams > 0 AND length_cm > 0 AND width_cm > 0 AND height_cm > 0");
                t.HasCheckConstraint("ck_shipments_value", "declared_value_minor >= 0");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Carrier).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TrackingNumber).HasMaxLength(100).IsRequired();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.OrderId);
            b.HasIndex(x => new { x.Carrier, x.TrackingNumber }).IsUnique();
            b.HasIndex(x => x.Status).HasFilter("status IN ('LabelCreated', 'HandedOver', 'InTransit', 'Exception')")
                .HasDatabaseName("ix_shipments_active");
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Events).WithOne().HasForeignKey(e => e.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(x => x.IsActive);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<TrackingEvent>(b =>
        {
            b.ToTable("tracking_events", Schema);
            // Ids are assigned in the domain; without this EF treats new events added to a tracked shipment as
            // existing rows and issues UPDATEs (which the append-only grant rightly rejects).
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasKey(x => x.Id);
            b.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.Location).HasMaxLength(200);
            b.HasIndex(x => new { x.ShipmentId, x.ExternalId }).IsUnique();
        });
    }
}
