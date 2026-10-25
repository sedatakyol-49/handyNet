using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ankauf.Infrastructure;

public sealed class AnkaufModel : IModuleModel
{
    public const string Schema = "ankauf";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceList>(b =>
        {
            b.ToTable("price_lists", Schema, t => t.HasCheckConstraint("ck_price_lists_validity", "valid_until > valid_from"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Label).HasMaxLength(100).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            b.Property(x => x.FileSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.SheetName).HasMaxLength(100).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SkippedRows).HasJsonConversion();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.ImportedAt);
        });

        modelBuilder.Entity<PriceListEntry>(b =>
        {
            b.ToTable("price_list_entries", Schema, t => t.HasCheckConstraint("ck_price_list_entries_price", "price_minor > 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.ModelKey).HasMaxLength(300).IsRequired();
            b.Property(x => x.Brand).HasMaxLength(100).IsRequired();
            b.Property(x => x.Model).HasMaxLength(200).IsRequired();
            b.Property(x => x.Storage).HasMaxLength(50);
            b.Property(x => x.Change).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.BecomesPrice);
            b.HasIndex(x => new { x.PriceListId, x.ModelKey }).IsUnique();
            b.HasIndex(x => new { x.PriceListId, x.Decision });
            b.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceVersion>(b =>
        {
            b.ToTable("price_versions", Schema, t =>
            {
                t.HasCheckConstraint("ck_price_versions_price", "price_minor > 0");
                t.HasCheckConstraint("ck_price_versions_validity", "valid_until > valid_from");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.ModelKey).HasMaxLength(300).IsRequired();
            b.Property(x => x.Brand).HasMaxLength(100).IsRequired();
            b.Property(x => x.Model).HasMaxLength(200).IsRequired();
            b.Property(x => x.Storage).HasMaxLength(50);
            b.HasIndex(x => new { x.ModelKey, x.ValidFrom });
            b.HasIndex(x => new { x.PriceListId, x.ModelKey }).IsUnique();
            b.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConditionPolicy>(b =>
        {
            b.ToTable("condition_policies", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Settings).HasJsonConversion();
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => x.EffectiveFrom);
        });

        modelBuilder.Entity<Intake>(b =>
        {
            b.ToTable("intakes", Schema, t =>
            {
                t.HasCheckConstraint("ck_intakes_paid", "paid_minor IS NULL OR paid_minor > 0");
                // Anonymized = exactly the fixed placeholder of SellerDetails.Anonymized (serialized camelCase, enum as number).
                t.HasCheckConstraint("ck_intakes_anonymized",
                    "anonymized_at IS NULL OR seller = '{\"firstName\":\"anonymisiert\",\"lastName\":\"anonymisiert\",\"street\":\"anonymisiert\","
                    + "\"postalCode\":\"00000\",\"city\":\"anonymisiert\",\"birthDate\":\"1900-01-01\",\"idDocumentType\":0,"
                    + "\"idDocumentNumber\":\"ANONYMISIERT\"}'::jsonb");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Imei).HasMaxLength(15).IsFixedLength().IsRequired();
            // Personal data of the seller, kept as one document; see docs/operations.md for retention.
            b.Property(x => x.Seller).HasJsonConversion();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.InspectedCondition).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.InspectedDefects).HasConversion(
                    v => v.Select(d => d.ToString()).ToArray(),
                    v => v.Select(Enum.Parse<DeviceDefect>).ToList(),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<DeviceDefect>>(
                        (a, c) => a!.SequenceEqual(c!), v => v.Aggregate(0, (h, d) => HashCode.Combine(h, d)), v => v.ToList()))
                .HasColumnType("text[]");
            b.Property(x => x.InspectionNotes).HasMaxLength(2000);
            b.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.RejectionReason).HasMaxLength(1000);
            b.Property(x => x.CaptureMethod).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.IsAnonymized);
            b.Ignore(x => x.RetentionStart);
            // A quote can be turned into at most one live intake.
            b.HasIndex(x => x.QuoteId).IsUnique().HasFilter("status <> 'Rejected'").HasDatabaseName("ux_intakes_one_per_quote");
            b.HasIndex(x => new { x.OrganizationId, x.ReceivedAt });
            b.HasIndex(x => x.Imei);
            b.HasOne<Quote>().WithMany().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
            // A self check-in feeds at most one intake.
            b.HasIndex(x => x.CheckInId).IsUnique().HasFilter("check_in_id IS NOT NULL");
            b.HasOne<CheckIn>().WithMany().HasForeignKey(x => x.CheckInId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<IntakeSignature>().WithMany().HasForeignKey(x => x.SignatureId).OnDelete(DeleteBehavior.Restrict);
            // Cash taken from a till: at most one payout per intake (also unique in pos.cash_movements by source reference).
            b.HasIndex(x => x.CashMovementId).IsUnique().HasFilter("cash_movement_id IS NOT NULL");
        });

        modelBuilder.Entity<IntakeSignature>(b =>
        {
            b.ToTable("intake_signatures", Schema, t =>
            {
                t.HasCheckConstraint("ck_intake_signatures_image", "image IS NOT NULL OR anonymized_at IS NOT NULL");
                t.HasCheckConstraint("ck_intake_signatures_purchase",
                    "purpose <> 'Purchase' OR (agreed_paid_minor > 0 AND agreed_payment_method IS NOT NULL)");
                t.HasCheckConstraint("ck_intake_signatures_hashes",
                    "length(image_sha256) = 64 AND length(data_hash) = 64 AND length(binding_hash) = 64");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ImageSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.DataHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.BindingHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.DeclarationVersion).HasMaxLength(50).IsRequired();
            b.Property(x => x.AgreedPaymentMethod).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.OrganizationId, x.IntakeId, x.SignedAt });
            b.HasOne<Intake>().WithMany().HasForeignKey(x => x.IntakeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CheckIn>(b =>
        {
            b.ToTable("checkins", Schema, t =>
            {
                t.HasCheckConstraint("ck_checkins_expiry", "expires_at > created_at");
                t.HasCheckConstraint("ck_checkins_submitted", "submitted_at IS NOT NULL OR (seller IS NULL AND signature_image IS NULL)");
                t.HasCheckConstraint("ck_checkins_purged", "purged_at IS NULL OR (seller IS NULL AND signature_image IS NULL)");
                t.HasCheckConstraint("ck_checkins_consumed", "(consumed_at IS NULL) = (intake_id IS NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.ShopName).HasMaxLength(CheckIn.MaxShopNameLength).IsRequired();
            b.Property(x => x.Seller!).HasJsonConversion();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        });

        modelBuilder.Entity<Quote>(b =>
        {
            b.ToTable("quotes", Schema, t =>
                t.HasCheckConstraint("ck_quotes_amounts", "reference_purchase_price_minor >= 0 AND platform_offer_minor >= 0 AND max_consumer_price_minor >= 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.ModelKey).HasMaxLength(300).IsRequired();
            b.Property(x => x.Brand).HasMaxLength(100).IsRequired();
            b.Property(x => x.Model).HasMaxLength(200).IsRequired();
            b.Property(x => x.Storage).HasMaxLength(50);
            b.Property(x => x.Condition).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Defects).HasConversion(
                    v => v.Select(d => d.ToString()).ToArray(),
                    v => v.Select(Enum.Parse<DeviceDefect>).ToList(),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<DeviceDefect>>(
                        (a, c) => a!.SequenceEqual(c!), v => v.Aggregate(0, (h, d) => HashCode.Combine(h, d)), v => v.ToList()))
                .HasColumnType("text[]");
            b.Property(x => x.Imei).HasMaxLength(15).IsFixedLength();
            b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            b.HasOne<PriceVersion>().WithMany().HasForeignKey(x => x.PriceVersionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ConditionPolicy>().WithMany().HasForeignKey(x => x.ConditionPolicyId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
