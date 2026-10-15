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
