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
            {
                t.HasCheckConstraint("ck_organizations_has_capability", "is_wholesaler OR is_retailer OR is_repair_provider");
                // Company data maintained by the tenant (ADR-0014); the domain checks the IBAN checksum, the database the shape.
                t.HasCheckConstraint("ck_organizations_payment_terms", "payment_terms_days BETWEEN 0 AND 365");
                t.HasCheckConstraint("ck_organizations_email_language", "email_language IN ('de', 'en', 'tr', 'es', 'ru', 'ar', 'fa')");
                t.HasCheckConstraint("ck_organizations_iban", "iban ~ '^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$'");
                t.HasCheckConstraint("ck_organizations_bic", "bic ~ '^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$'");
                t.HasCheckConstraint("ck_organizations_bank_needs_iban", "iban IS NOT NULL OR (bic IS NULL AND bank_name IS NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.EmailLanguage).HasMaxLength(2).HasDefaultValue("de").IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.VatId).HasMaxLength(20);
            b.Property(x => x.Street).HasMaxLength(200).IsRequired();
            b.Property(x => x.PostalCode).HasMaxLength(10).IsRequired();
            b.Property(x => x.City).HasMaxLength(100).IsRequired();
            b.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.StatusReason).HasMaxLength(1000);
            b.Property(x => x.TaxNumber).HasMaxLength(Organization.MaxTaxNumberLength);
            b.Property(x => x.ContactEmail).HasMaxLength(Organization.MaxContactEmailLength);
            b.Property(x => x.ContactPhone).HasMaxLength(Organization.MaxContactPhoneLength);
            b.Property(x => x.Website).HasMaxLength(Organization.MaxWebsiteLength);
            b.Property(x => x.ReceiptHeader).HasMaxLength(CompanyProfileRules.MaxReceiptHeaderLength);
            b.Property(x => x.ReceiptFooter).HasMaxLength(CompanyProfileRules.MaxReceiptFooterLength);
            b.Property(x => x.BankName).HasMaxLength(CompanyProfileRules.MaxBankNameLength);
            b.Property(x => x.Iban).HasMaxLength(CompanyProfileRules.MaxIbanLength);
            b.Property(x => x.Bic).HasMaxLength(CompanyProfileRules.MaxBicLength);
            b.Property(x => x.ManagingDirector).HasMaxLength(CompanyProfileRules.MaxManagingDirectorLength);
            b.Property(x => x.RegisterCourt).HasMaxLength(CompanyProfileRules.MaxRegisterCourtLength);
            b.Property(x => x.RegisterNumber).HasMaxLength(CompanyProfileRules.MaxRegisterNumberLength);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.VatId).IsUnique().HasFilter("vat_id IS NOT NULL");
            b.HasIndex(x => x.Status);
            b.HasIndex(x => x.CreatedAt);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Modules).WithOne().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Modules).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasMany(x => x.StatusChanges).WithOne().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.StatusChanges).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(x => x.Profile);
            b.Ignore(x => x.CompanyProfile);
            b.Ignore(x => x.DomainEvents);
        });

        // Append-only status history (UPDATE/DELETE revoked from the app role). Not tenant-owned: platform data about the
        // organization, like the organization row itself (ADR-0003 §5).
        modelBuilder.Entity<OrganizationStatusChange>(b =>
        {
            b.ToTable("organization_status_changes", Schema);
            b.HasKey(x => x.Id);
            // Ids are assigned in the domain; added through the organization's collection they must be inserted, not updated.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Reason).HasMaxLength(1000);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30).HasDefaultValue(OrganizationHistoryKind.StatusChange)
                .HasSentinel((OrganizationHistoryKind)(-1));
            b.HasIndex(x => new { x.OrganizationId, x.ChangedAt });
            b.HasOne<User>().WithMany().HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.Restrict);
        });

        // Company logo (ADR-0014): tenant-owned (RLS: everyone reads — logos are public on e-receipts —, only the own
        // organization inserts/updates; no DELETE). One active logo per organization.
        modelBuilder.Entity<OrganizationLogo>(b =>
        {
            b.ToTable("organization_logos", Schema, t =>
                t.HasCheckConstraint("ck_organization_logos_removed", "(removed_at IS NULL) = (removed_by IS NULL)"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ScreenStorageKey).HasMaxLength(400);
            b.Property(x => x.PrintStorageKey).HasMaxLength(400);
            b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.OriginalFormat).HasConversion<string>().HasMaxLength(10);
            b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("removed_at IS NULL").HasDatabaseName("ux_organization_logos_active");
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.RemovedBy).OnDelete(DeleteBehavior.Restrict);
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
