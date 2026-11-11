using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Identity.Domain;
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
            b.ToTable("registers", Schema, t =>
            {
                t.HasCheckConstraint("ck_registers_next_number", "next_receipt_number >= 1");
                t.HasCheckConstraint("ck_registers_next_z_number", "next_z_number >= 1");
                t.HasCheckConstraint("ck_registers_auto_lock", $"auto_lock_minutes BETWEEN 1 AND {Register.MaxAutoLockMinutes}");
            });
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
                t.HasCheckConstraint("ck_cash_sessions_z_number", "(status = 'Open' AND z_number IS NULL) OR (status = 'Closed' AND z_number >= 1)");
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
            // Z numbers are unique per register (gapless with the allocation triggers).
            b.HasIndex(x => new { x.RegisterId, x.ZNumber }).IsUnique().HasFilter("z_number IS NOT NULL").HasDatabaseName("ux_cash_sessions_z_number");
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
            b.Property(x => x.SourceReference).HasMaxLength(CashMovement.MaxSourceReferenceLength);
            b.HasIndex(x => new { x.OrganizationId, x.SessionId });
            // A source (e.g. an Ankauf intake) is paid out of a till at most once.
            b.HasIndex(x => new { x.OrganizationId, x.SourceReference }).IsUnique().HasFilter("source_reference IS NOT NULL")
                .HasDatabaseName("ux_cash_movements_source");
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SessionId })
                .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Receipt>(b =>
        {
            b.ToTable("receipts", Schema, t =>
            {
                t.HasCheckConstraint("ck_receipts_number", "number >= 1");
                t.HasCheckConstraint("ck_receipts_rounding", "rounding_minor BETWEEN -2 AND 2");
                t.HasCheckConstraint("ck_receipts_offline",
                    "(offline_recorded_at IS NULL AND offline_reference IS NULL) "
                    + "OR (offline_recorded_at IS NOT NULL AND offline_reference ~ '^[A-Za-z0-9-]{1,40}$' AND kind = 'Sale')");
                t.HasCheckConstraint("ck_receipts_totals", "total_net_minor + total_vat_minor = total_gross_minor AND change_minor >= 0");
                t.HasCheckConstraint("ck_receipts_kind",
                    "(kind = 'Sale' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL AND sales_invoice_id IS NULL) "
                    + "OR (kind = 'InvoicePayment' AND total_gross_minor > 0 AND referenced_receipt_id IS NULL AND sales_invoice_id IS NOT NULL) "
                    + "OR (kind IN ('Storno', 'Return') AND total_gross_minor <= 0 AND change_minor = 0 AND referenced_receipt_id IS NOT NULL AND sales_invoice_id IS NULL)");
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
            b.Property(x => x.FiscalMode).HasMaxLength(30);
            b.Property(x => x.FiscalClientId).HasMaxLength(FiscalConfiguration.MaxClientIdLength);
            b.Property(x => x.FiscalProcessType).HasMaxLength(30);
            b.Property(x => x.FiscalProcessData).HasMaxLength(1000);
            b.Property(x => x.FiscalSignatureAlgorithm).HasMaxLength(50);
            b.Property(x => x.FiscalTimeFormat).HasMaxLength(30);
            b.Property(x => x.FiscalPublicKey).HasMaxLength(500);
            b.Property(x => x.FiscalQrPayload).HasMaxLength(4000);
            b.Property(x => x.FiscalError).HasMaxLength(500);
            b.Property(x => x.OfflineReference).HasMaxLength(OfflineRecord.MaxReferenceLength);
            b.Ignore(x => x.Offline);
            // The device's offline reference is booked at most once per register.
            b.HasIndex(x => new { x.RegisterId, x.OfflineReference }).IsUnique().HasFilter("offline_reference IS NOT NULL")
                .HasDatabaseName("ux_receipts_offline_reference");
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
            b.HasOne<Pricelist>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PricelistId })
                .HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.SalesInvoiceId).HasFilter("sales_invoice_id IS NOT NULL");
            b.HasOne<SalesInvoice>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesInvoiceId })
                .HasPrincipalKey(i => new { i.OrganizationId, i.Id }).OnDelete(DeleteBehavior.Restrict);
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
                t.HasCheckConstraint("ck_receipt_lines_product",
                    "(product_id IS NOT NULL AND tax_treatment <> 'NotTaxable') OR (product_id IS NULL AND tax_treatment = 'NotTaxable' AND stock_unit_id IS NULL AND vat_minor = 0)");
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
            b.HasIndex(x => new { x.OrganizationId, x.PricelistId }).HasFilter("pricelist_id IS NOT NULL");
            b.HasOne<Pricelist>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PricelistId })
                .HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Pricelist>(b =>
        {
            b.ToTable("pricelists", Schema, t =>
            {
                t.HasCheckConstraint("ck_pricelists_discount", $"discount_basis_points BETWEEN 0 AND {Pricelist.MaxDiscountBasisPoints}");
                t.HasCheckConstraint("ck_pricelists_validity", "valid_from IS NULL OR valid_to IS NULL OR valid_from <= valid_to");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(Pricelist.MaxNameLength).IsRequired();
            b.Property(x => x.Version).IsRowVersion();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<PricelistItem>(b =>
        {
            b.ToTable("pricelist_items", Schema, t => t.HasCheckConstraint("ck_pricelist_items_price", "price_minor > 0"));
            b.HasKey(x => new { x.PricelistId, x.ProductId });
            b.HasIndex(x => new { x.OrganizationId, x.ProductId });
            b.HasOne<Pricelist>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PricelistId })
                .HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashierPin>(b =>
        {
            b.ToTable("cashier_pins", Schema, t =>
            {
                t.HasCheckConstraint("ck_cashier_pins_attempts", $"failed_attempts BETWEEN 0 AND {CashierPin.MaxFailedAttempts}");
                t.HasCheckConstraint("ck_cashier_pins_hash", "pin_hash IS NULL OR pin_hash LIKE 'pbkdf2-sha256$%'");
            });
            b.HasKey(x => new { x.OrganizationId, x.UserId });
            b.Property(x => x.PinHash).HasMaxLength(200);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.HasPin);
            b.Ignore(x => x.RemainingAttempts);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashierSession>(b =>
        {
            b.ToTable("cashier_sessions", Schema, t =>
            {
                t.HasCheckConstraint("ck_cashier_sessions_times", "expires_at > created_at");
                t.HasCheckConstraint("ck_cashier_sessions_end", "(ended_at IS NULL) = (ended_reason IS NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.EndedReason).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId, x.DeviceUserId }).HasFilter("ended_at IS NULL");
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CashierUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.DeviceUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PosApproval>(b =>
        {
            b.ToTable("approvals", Schema, t =>
            {
                t.HasCheckConstraint("ck_approvals_permissions", "cardinality(permissions) >= 1");
                t.HasCheckConstraint("ck_approvals_times", "expires_at > created_at");
                t.HasCheckConstraint("ck_approvals_use",
                    "(used_at IS NULL AND used_for IS NULL AND receipt_id IS NULL AND cash_movement_id IS NULL AND session_id IS NULL AND sales_invoice_id IS NULL) "
                    + "OR (used_at IS NOT NULL AND used_for IS NOT NULL AND num_nonnulls(receipt_id, cash_movement_id, session_id, sales_invoice_id) = 1)");
                t.HasCheckConstraint("ck_approvals_not_self", "approver_user_id <> requested_by_user_id");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Permissions).HasColumnType("text[]");
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.UsedFor).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.ReceiptId).HasFilter("receipt_id IS NOT NULL");
            b.HasIndex(x => x.CashMovementId).HasFilter("cash_movement_id IS NOT NULL");
            b.HasIndex(x => x.SessionId).HasFilter("session_id IS NOT NULL");
            b.HasIndex(x => x.SalesInvoiceId).HasFilter("sales_invoice_id IS NOT NULL");
            b.HasOne<SalesInvoice>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesInvoiceId })
                .HasPrincipalKey(i => new { i.OrganizationId, i.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CashMovement>().WithMany().HasForeignKey(x => x.CashMovementId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SessionId })
                .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReceiptEmail>(b =>
        {
            b.ToTable("receipt_emails", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Recipient).HasMaxLength(ReceiptEmail.MaxEmailLength).IsRequired();
            b.Ignore(x => x.DomainEvents);
            b.HasIndex(x => new { x.OrganizationId, x.ReceiptId });
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PosInvoice>(b =>
        {
            b.ToTable("invoices", Schema, t =>
            {
                t.HasCheckConstraint("ck_invoices_number", "sequence_number >= 1 AND year BETWEEN 2000 AND 9999 "
                    + "AND number = 'RE-' || year::text || '-' || lpad(sequence_number::text, 6, '0')");
                t.HasCheckConstraint("ck_invoices_totals", "total_gross_minor > 0 AND total_vat_minor >= 0 AND total_net_minor + total_vat_minor = total_gross_minor");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Number).HasMaxLength(20).IsRequired();
            b.Property(x => x.Customer).HasJsonConversion().IsRequired();
            b.Property(x => x.CustomerName).HasComputedColumnSql("customer ->> 'name'", stored: true);
            b.Property(x => x.CustomerCompany).HasComputedColumnSql("customer ->> 'company'", stored: true);
            b.Property(x => x.Document).HasJsonConversion().IsRequired();
            b.HasIndex(x => x.ReceiptId).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.Year, x.SequenceNumber }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique();
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PosInvoiceSequence>(b =>
        {
            b.ToTable("invoice_sequences", Schema, t => t.HasCheckConstraint("ck_invoice_sequences_next", "next_number >= 2"));
            b.HasKey(x => new { x.OrganizationId, x.Year });
        });

        modelBuilder.Entity<ReceiptLink>(b =>
        {
            b.ToTable("receipt_links", Schema, t => t.HasCheckConstraint("ck_receipt_links_expiry", "expires_at > created_at"));
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.ReceiptId });
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DisplayLink>(b =>
        {
            b.ToTable("display_links", Schema, t => t.HasCheckConstraint("ck_display_links_expiry", "expires_at > created_at"));
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            b.HasIndex(x => x.TokenHash).IsUnique();
            // A register has at most one link that was not revoked.
            b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_display_links_one_active");
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId });
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureFiscal(modelBuilder);
        ConfigureSalesDocuments(modelBuilder);

        modelBuilder.Entity<ReceiptReprint>(b =>
        {
            b.ToTable("receipt_reprints", Schema, t => t.HasCheckConstraint("ck_receipt_reprints_sequence", "sequence >= 1"));
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ReceiptId, x.Sequence }).IsUnique();
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>Quotations, sales invoices outside the till, their payments and emails (ADR-0015).</summary>
    private static void ConfigureSalesDocuments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Quotation>(b =>
        {
            b.ToTable("quotations", Schema, t =>
            {
                t.HasCheckConstraint("ck_quotations_number", "sequence_number >= 1 AND year BETWEEN 2000 AND 9999 AND number = 'AN-' || year::text || '-' "
                    + "|| CASE WHEN sequence_number < 10000 THEN lpad(sequence_number::text, 4, '0') ELSE sequence_number::text END");
                t.HasCheckConstraint("ck_quotations_totals", "total_net_minor + total_vat_minor = total_gross_minor AND total_gross_minor >= 0");
                t.HasCheckConstraint("ck_quotations_valid_until", "valid_until >= issue_date");
                t.HasCheckConstraint("ck_quotations_converted", "converted_invoice_id IS NULL OR status = 'Accepted'");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Number).HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PriceMode).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Customer).HasJsonConversion().IsRequired();
            b.Property(x => x.CustomerName).HasComputedColumnSql("customer ->> 'name'", stored: true);
            b.Property(x => x.CustomerCompany).HasComputedColumnSql("customer ->> 'company'", stored: true);
            b.Property(x => x.Lines).HasJsonConversion().IsRequired();
            b.Property(x => x.Vat).HasJsonConversion().IsRequired();
            b.Property(x => x.Notes).HasMaxLength(Quotation.MaxNotesLength);
            b.Property(x => x.Version).IsRowVersion();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            b.HasIndex(x => new { x.OrganizationId, x.Year, x.SequenceNumber }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.IssueDate });
            b.HasIndex(x => new { x.OrganizationId, x.CustomerId }).HasFilter("customer_id IS NOT NULL");
            b.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesInvoice>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ConvertedInvoiceId })
                .HasPrincipalKey(i => new { i.OrganizationId, i.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuotationSequence>(b =>
        {
            b.ToTable("quotation_sequences", Schema, t => t.HasCheckConstraint("ck_quotation_sequences_next", "next_number >= 2"));
            b.HasKey(x => new { x.OrganizationId, x.Year });
        });

        modelBuilder.Entity<SalesInvoice>(b =>
        {
            b.ToTable("sales_invoices", Schema, t =>
            {
                // Drafts and discarded drafts carry no number; issued rows the number of the shared series and their snapshot.
                t.HasCheckConstraint("ck_sales_invoices_number",
                    "(status IN ('Draft', 'Discarded') AND number IS NULL AND year IS NULL AND sequence_number IS NULL AND issued_at IS NULL AND document IS NULL) "
                    + "OR (status = 'Issued' AND sequence_number >= 1 AND year BETWEEN 2000 AND 9999 "
                    + "AND number = 'RE-' || year::text || '-' || lpad(sequence_number::text, 6, '0') AND invoice_date IS NOT NULL AND due_date IS NOT NULL "
                    + "AND service_date IS NOT NULL AND customer IS NOT NULL AND document IS NOT NULL AND issued_at IS NOT NULL AND issued_by IS NOT NULL)");
                t.HasCheckConstraint("ck_sales_invoices_totals",
                    "total_net_minor + total_vat_minor = total_gross_minor "
                    + "AND ((kind = 'Invoice' AND original_invoice_id IS NULL AND total_gross_minor >= 0 AND total_vat_minor >= 0 AND (status <> 'Issued' OR total_gross_minor > 0)) "
                    + "OR (kind = 'CreditNote' AND status = 'Issued' AND original_invoice_id IS NOT NULL AND total_gross_minor < 0 AND total_vat_minor <= 0 AND reason IS NOT NULL))");
                t.HasCheckConstraint("ck_sales_invoices_terms", $"payment_term_days BETWEEN 0 AND {SalesInvoice.MaxPaymentTermDays} AND (due_date IS NULL OR due_date >= invoice_date)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PriceMode).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Number).HasMaxLength(20);
            b.Property(x => x.Customer!).HasJsonConversion();
            b.Property(x => x.CustomerName).HasComputedColumnSql("customer ->> 'name'", stored: true);
            b.Property(x => x.CustomerCompany).HasComputedColumnSql("customer ->> 'company'", stored: true);
            b.Property(x => x.Lines).HasJsonConversion().IsRequired();
            b.Property(x => x.Vat).HasJsonConversion().IsRequired();
            b.Property(x => x.MarginDetails!).HasJsonConversion();
            b.Property(x => x.Document!).HasJsonConversion();
            b.Property(x => x.Notes).HasMaxLength(SalesInvoice.MaxNotesLength);
            b.Property(x => x.Reason).HasMaxLength(SalesInvoice.MaxReasonLength);
            b.Property(x => x.Version).IsRowVersion();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            b.HasIndex(x => new { x.OrganizationId, x.Year, x.SequenceNumber }).IsUnique().HasFilter("sequence_number IS NOT NULL");
            b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasFilter("number IS NOT NULL");
            // An invoice is cancelled at most once.
            b.HasIndex(x => x.OriginalInvoiceId).IsUnique().HasFilter("original_invoice_id IS NOT NULL").HasDatabaseName("ux_sales_invoices_one_credit_note");
            b.HasIndex(x => new { x.OrganizationId, x.Status, x.DueDate });
            b.HasIndex(x => new { x.OrganizationId, x.CustomerId }).HasFilter("customer_id IS NOT NULL");
            b.HasIndex(x => x.QuotationId).HasFilter("quotation_id IS NOT NULL");
            b.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
                .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesInvoice>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OriginalInvoiceId })
                .HasPrincipalKey(i => new { i.OrganizationId, i.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesInvoicePayment>(b =>
        {
            b.ToTable("sales_invoice_payments", Schema, t =>
            {
                t.HasCheckConstraint("ck_sales_invoice_payments_amount", "amount_minor > 0");
                // Cash in the shop always goes through the till (a receipt).
                t.HasCheckConstraint("ck_sales_invoice_payments_cash", "method <> 'Cash' OR receipt_id IS NOT NULL");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Reference).HasMaxLength(SalesInvoicePayment.MaxReferenceLength);
            b.HasIndex(x => new { x.OrganizationId, x.InvoiceId });
            b.HasIndex(x => x.ReceiptId).IsUnique().HasFilter("receipt_id IS NOT NULL");
            b.HasOne<SalesInvoice>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.InvoiceId })
                .HasPrincipalKey(i => new { i.OrganizationId, i.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesDocumentEmail>(b =>
        {
            b.ToTable("sales_document_emails", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Recipient).HasMaxLength(ReceiptEmail.MaxEmailLength).IsRequired();
            b.Ignore(x => x.DomainEvents);
            b.HasIndex(x => new { x.OrganizationId, x.DocumentType, x.DocumentId });
        });
    }

    /// <summary>Fiscal layer (ADR-0012): configuration, simulated TSE, outages, signatures of non-sale events.</summary>
    private static void ConfigureFiscal(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FiscalConfiguration>(b =>
        {
            b.ToTable("fiscal_configurations", Schema, t =>
            {
                t.HasCheckConstraint("ck_fiscal_configurations_org_mode", "register_id IS NOT NULL OR mode IS NOT NULL");
                t.HasCheckConstraint("ck_fiscal_configurations_simulated",
                    $"(simulated_latency_ms IS NULL OR simulated_latency_ms BETWEEN 0 AND {FiscalConfiguration.MaxLatencyMs}) "
                    + "AND (simulated_failure_rate_percent IS NULL OR simulated_failure_rate_percent BETWEEN 0 AND 100)");
                t.HasCheckConstraint("ck_fiscal_configurations_fiskaly_environment", "fiskaly_environment IS NULL OR fiskaly_environment IN ('Sandbox', 'Production')");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.SimulatedAlgorithm).HasMaxLength(50);
            b.Property(x => x.ClientId).HasMaxLength(FiscalConfiguration.MaxClientIdLength);
            b.Property(x => x.FiskalyEnvironment).HasMaxLength(20);
            b.Property(x => x.FiskalyCredentialReference).HasMaxLength(FiscalConfiguration.MaxReferenceLength);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.HasFiskalyCredentials);
            // One organization row and at most one row per register.
            b.HasIndex(x => new { x.OrganizationId, x.RegisterId }).IsUnique().AreNullsDistinct(false)
                .HasDatabaseName("ux_fiscal_configurations_scope");
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SimulatedTse>(b =>
        {
            b.ToTable("simulated_tses", Schema, t => t.HasCheckConstraint("ck_simulated_tses_counters", "transaction_counter >= 0 AND signature_counter >= 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.SerialNumber).HasMaxLength(64).IsRequired();
            b.Property(x => x.PublicKey).HasMaxLength(500).IsRequired();
            b.Property(x => x.SignatureAlgorithm).HasMaxLength(50).IsRequired();
            b.Property(x => x.TimeFormat).HasMaxLength(30).IsRequired();
            b.Property(x => x.ProtectedPrivateKey).IsRequired();
            b.HasIndex(x => x.OrganizationId).IsUnique();
            b.HasIndex(x => x.SerialNumber).IsUnique();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
        });

        modelBuilder.Entity<SimulatedTseTransaction>(b =>
        {
            b.ToTable("simulated_tse_transactions", Schema, t =>
            {
                t.HasCheckConstraint("ck_simulated_tse_transactions_number", "number >= 1 AND start_signature_counter >= 1");
                t.HasCheckConstraint("ck_simulated_tse_transactions_finished",
                    "state = 'Active' OR (finished_at IS NOT NULL AND process_type IS NOT NULL AND process_data IS NOT NULL "
                    + "AND signature IS NOT NULL AND signature_counter > start_signature_counter)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).HasMaxLength(FiscalConfiguration.MaxClientIdLength).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.StartSignature).HasMaxLength(200).IsRequired();
            b.Property(x => x.ProcessType).HasMaxLength(30);
            b.Property(x => x.ProcessData).HasMaxLength(1000);
            b.Property(x => x.Signature).HasMaxLength(200);
            b.HasIndex(x => new { x.TseId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TseId, x.TransactionKey }).IsUnique();
            b.HasOne<SimulatedTse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TseId })
                .HasPrincipalKey(t => new { t.OrganizationId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FiscalOutage>(b =>
        {
            b.ToTable("fiscal_outages", Schema, t => t.HasCheckConstraint("ck_fiscal_outages_period", "ended_at IS NULL OR ended_at >= started_at"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Reason).HasMaxLength(FiscalOutage.MaxReasonLength).IsRequired();
            // At most one open outage per register.
            b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("ended_at IS NULL").HasDatabaseName("ux_fiscal_outages_one_open_per_register");
            b.HasIndex(x => new { x.OrganizationId, x.StartedAt });
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SessionId })
                .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FiscalRecord>(b =>
        {
            b.ToTable("fiscal_records", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Data).HasJsonConversion().IsRequired();
            b.HasIndex(x => new { x.Kind, x.ReferenceId }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.SessionId });
            b.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SessionId })
                .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Register>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RegisterId })
                .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        PrinterProfileModel.Configure(modelBuilder);
        CardTerminalModel.Configure(modelBuilder);
    }
}
