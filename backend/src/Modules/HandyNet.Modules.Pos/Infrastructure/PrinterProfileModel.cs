using HandyNet.Modules.Pos.Domain;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>EF mapping of <c>pos.printer_profiles</c> (ADR-0018); RLS, grants and the default index are in the migration.</summary>
internal static class PrinterProfileModel
{
    public static void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<PrinterProfile>(b =>
        {
            b.ToTable("printer_profiles", PosModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_printer_profiles_connection",
                    "connection IN ('Browser', 'EpsonEpos', 'StarWebPrnt', 'EscPosBridge', 'WebUsb', 'WebSerial')");
                t.HasCheckConstraint("ck_printer_profiles_codepage", "codepage IN ('Cp858', 'Pc437')");
                t.HasCheckConstraint("ck_printer_profiles_paper", "paper_width_mm IN (58, 80)");
                t.HasCheckConstraint("ck_printer_profiles_chars",
                    $"chars_per_line BETWEEN {PrinterProfile.MinCharsPerLine} AND {PrinterProfile.MaxCharsPerLine}");
                t.HasCheckConstraint("ck_printer_profiles_copies", $"copies BETWEEN 1 AND {PrinterProfile.MaxCopies}");
                t.HasCheckConstraint("ck_printer_profiles_port", "port IS NULL OR port BETWEEN 1 AND 65535");
                t.HasCheckConstraint("ck_printer_profiles_usb",
                    "(usb_vendor_id IS NULL OR usb_vendor_id BETWEEN 0 AND 65535) AND (usb_product_id IS NULL OR usb_product_id BETWEEN 0 AND 65535)");
                // Network connections need a host; the bridge also needs its URL.
                t.HasCheckConstraint("ck_printer_profiles_network",
                    "connection NOT IN ('EpsonEpos', 'StarWebPrnt', 'EscPosBridge') OR host IS NOT NULL");
                t.HasCheckConstraint("ck_printer_profiles_bridge", "connection <> 'EscPosBridge' OR bridge_url IS NOT NULL");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(PrinterProfile.MaxNameLength).IsRequired();
            b.Property(x => x.Connection).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Codepage).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Host).HasMaxLength(PrinterProfile.MaxHostLength);
            b.Property(x => x.EposDeviceId).HasMaxLength(PrinterProfile.MaxDeviceIdLength);
            b.Property(x => x.BridgeUrl).HasMaxLength(PrinterProfile.MaxBridgeUrlLength);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.RegisterId, x.Name }).IsUnique();
            // At most one default profile per register.
            b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_printer_profiles_one_default");
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId });
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
}
