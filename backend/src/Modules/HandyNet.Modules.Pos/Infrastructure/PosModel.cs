using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Infrastructure;

public sealed class PosModel : IModuleModel
{
    public const string Schema = "pos";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Register>(b =>
        {
            b.ToTable("registers", Schema, t => t.HasCheckConstraint("ck_registers_next_number", "next_receipt_number >= 1"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(Register.MaxNameLength).IsRequired();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            // A register sells from one of the organization's own warehouses (composite FK: never another tenant's).
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashSession>(b =>
        {
            b.ToTable("cash_sessions", Schema, t =>
            {
                t.HasCheckConstraint("ck_cash_sessions_opening", "opening_cash_minor >= 0");
                t.HasCheckConstraint("ck_cash_sessions_closed",
                    "status = 'Open' OR (closed_at IS NOT NULL AND closed_by IS NOT NULL AND summary IS NOT NULL AND counted_cash_minor >= 0 "
                    + "AND expected_cash_minor IS NOT NULL AND difference_minor = counted_cash_minor - expected_cash_minor)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ClosingNote).HasMaxLength(CashSession.MaxNoteLength);
            b.Property(x => x.Summary!).HasJsonConversion();
            b.Property(x => x.OpeningDenominations!).HasJsonConversion();
            b.Property(x => x.ClosingDenominations!).HasJsonConversion();
            b.Property(x => x.Version).IsRowVersion();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            // Only one open session per register.
            b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("status = 'Open'").HasDatabaseName("ux_cash_sessions_one_open_per_register");
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId, x.OpenedAt });
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashMovement>(b =>
        {
            b.ToTable("cash_movements", Schema, t => t.HasCheckConstraint("ck_cash_movements_amount", "amount_minor > 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reason).HasMaxLength(CashMovement.MaxReasonLength).IsRequired();
            b.Property(x => x.ReasonCode).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(x => new { x.OrganizationId, x.SessionId });
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SessionId })
                .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Receipt>(b =>
        {
            b.ToTable("receipts", Schema, t =>
            {
                t.HasCheckConstraint("ck_receipts_number", "number >= 1");
                t.HasCheckConstraint("ck_receipts_totals", "total_net_minor + total_vat_minor = total_gross_minor AND change_minor >= 0");
                t.HasCheckConstraint("ck_receipts_kind",
                    "(kind = 'Sale' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL) "
                    + "OR (kind IN ('Storno', 'Return') AND total_gross_minor <= 0 AND change_minor = 0 AND referenced_receipt_id IS NOT NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.IdempotencyKey).HasMaxLength(100);
            b.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Reason).HasMaxLength(Receipt.MaxReasonLength);
            b.Property(x => x.FiscalStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(x => x.FiscalNotice).HasMaxLength(200);
            b.Property(x => x.FiscalTseSerialNumber).HasMaxLength(200);
            b.Property(x => x.FiscalSignature).HasMaxLength(1000);
            b.Property(x => x.Document!).HasJsonConversion().IsRequired();
            b.Ignore(x => x.IsSealed);
            b.Ignore(x => x.Fiscal);
            b.Ignore(x => x.Customer);
            b.HasIndex(x => new { x.OrganizationId, x.CustomerId }).HasFilter("customer_id IS NOT NULL");
            b.HasIndex(x => new { x.OrganizationId, x.CreatedBy, x.CreatedAt });
            b.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            // Sequential numbers per register; together with the allocation trigger they are gapless.
            b.HasIndex(x => new { x.RegisterId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            // A sale can be cancelled (Storno) only once.
            b.HasIndex(x => x.ReferencedReceiptId).IsUnique().HasFilter("kind = 'Storno'").HasDatabaseName("ux_receipts_one_storno");
            b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SessionId })
                .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReferencedReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => new { l.OrganizationId, l.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Payments).WithOne().HasForeignKey(p => new { p.OrganizationId, p.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(x => x.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ReceiptLine>(b =>
        {
            b.ToTable("receipt_lines", Schema, t =>
            {
                t.HasCheckConstraint("ck_receipt_lines_quantity", "quantity <> 0 AND unit_price_minor > 0");
                t.HasCheckConstraint("ck_receipt_lines_amounts",
                    "line_gross_minor = unit_price_minor * quantity - discount_minor AND net_minor + vat_minor = line_gross_minor");
                t.HasCheckConstraint("ck_receipt_lines_margin",
                    "tax_treatment <> 'Margin25a' OR (stock_unit_id IS NOT NULL AND purchase_price_minor IS NOT NULL)");
            });
            b.HasKey(x => new { x.ReceiptId, x.LineNumber });
            b.Property(x => x.Imei).HasMaxLength(15).IsFixedLength();
            b.Property(x => x.Description).HasMaxLength(Receipt.MaxDescriptionLength).IsRequired();
            b.Property(x => x.TaxTreatment).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Note).HasMaxLength(Receipt.MaxNoteLength);
            b.HasIndex(x => x.StockUnitId);
            b.HasIndex(x => new { x.OrganizationId, x.ProductId });
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<StockUnit>().WithMany().HasForeignKey(x => x.StockUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReceiptPayment>(b =>
        {
            b.ToTable("receipt_payments", Schema, t =>
                t.HasCheckConstraint("ck_receipt_payments_amount", "amount_minor <> 0 AND (tendered_minor IS NULL OR tendered_minor >= amount_minor)"));
            b.HasKey(x => new { x.ReceiptId, x.Method });
            b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<PosPrice>(b =>
        {
            b.ToTable("prices", Schema, t => t.HasCheckConstraint("ck_prices_price", "price_minor IS NULL OR price_minor > 0"));
            b.HasKey(x => new { x.OrganizationId, x.ProductId });
            b.Property(x => x.Version).IsRowVersion();
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(Customer.MaxNameLength).IsRequired();
            b.Property(x => x.Company).HasMaxLength(Customer.MaxNameLength);
            b.Property(x => x.Email).HasMaxLength(Customer.MaxEmailLength);
            b.Property(x => x.Phone).HasMaxLength(Customer.MaxPhoneLength);
            b.Property(x => x.Street).HasMaxLength(Customer.MaxStreetLength);
            b.Property(x => x.PostalCode).HasMaxLength(Customer.MaxPostalCodeLength);
            b.Property(x => x.City).HasMaxLength(Customer.MaxCityLength);
            b.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
            b.Property(x => x.VatId).HasMaxLength(Customer.MaxVatIdLength);
            b.Property(x => x.Version).IsRowVersion();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            b.HasIndex(x => new { x.OrganizationId, x.Name });
        });

        modelBuilder.Entity<ReceiptReprint>(b =>
        {
            b.ToTable("receipt_reprints", Schema, t => t.HasCheckConstraint("ck_receipt_reprints_sequence", "sequence >= 1"));
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ReceiptId, x.Sequence }).IsUnique();
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
