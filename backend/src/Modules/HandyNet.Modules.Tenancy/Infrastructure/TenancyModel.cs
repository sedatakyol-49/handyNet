using HandyNet.Modules.Identity.Domain;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Tenancy.Infrastructure;

public sealed class TenancyModel : IModuleModel
{
    public const string Schema = "tenancy";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(b =>
        {
            b.ToTable("organizations", Schema, t =>
                t.HasCheckConstraint("ck_organizations_has_capability", "is_wholesaler OR is_retailer OR is_repair_provider"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.VatId).HasMaxLength(20);
            b.Property(x => x.Street).HasMaxLength(200).IsRequired();
            b.Property(x => x.PostalCode).HasMaxLength(10).IsRequired();
            b.Property(x => x.City).HasMaxLength(100).IsRequired();
            b.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.StatusReason).HasMaxLength(1000);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.VatId).IsUnique().HasFilter("vat_id IS NOT NULL");
            b.HasIndex(x => x.Status);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Modules).WithOne().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Modules).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<OrganizationModule>(b =>
        {
            b.ToTable("organization_modules", Schema);
            b.HasKey(x => new { x.OrganizationId, x.Module });
            b.Property(x => x.Module).HasConversion<string>().HasMaxLength(50);
        });

        modelBuilder.Entity<Membership>(b =>
        {
            b.ToTable("memberships", Schema, t =>
            {
                // Per-cashier POS rights exist only for staff (owners/managers always hold all of them).
                t.HasCheckConstraint("ck_memberships_pos_rights", "role = 'Staff' OR (pos_permissions IS NULL AND pos_max_discount_percent IS NULL)");
                t.HasCheckConstraint("ck_memberships_pos_max_discount", "pos_max_discount_percent BETWEEN 0 AND 100");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PosPermissions).HasColumnType("text[]");
            b.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            // Cross-module foreign key without navigation: referential integrity, no object coupling.
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
