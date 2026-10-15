using HandyNet.Modules.Invoicing.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Invoicing.Infrastructure;

/// <summary>
/// Platform-wide number counter per series (invoice / credit note) and year. Only advanced by
/// <c>InvoiceNumbers.AllocateAsync</c> inside the issuing transaction; the database keeps it gapless (migration).
/// </summary>
public sealed class NumberSequence
{
    private NumberSequence() { }

    public string Kind { get; private set; } = null!;
    public int Year { get; private set; }
    public long NextNumber { get; private set; }
}

public sealed class InvoicingModel : IModuleModel
{
    public const string Schema = "invoicing";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NumberSequence>(b =>
        {
            b.ToTable("number_sequences", Schema, t => t.HasCheckConstraint("ck_number_sequences_next", "next_number >= 1"));
            b.HasKey(x => new { x.Kind, x.Year });
            b.Property(x => x.Kind).HasMaxLength(20);
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoices", Schema, t =>
            {
                t.HasCheckConstraint("ck_invoices_totals", "total_net_minor + total_vat_minor = total_gross_minor");
                t.HasCheckConstraint("ck_invoices_kind",
                    "(kind = 'Invoice' AND total_net_minor > 0 AND total_vat_minor >= 0 AND referenced_invoice_id IS NULL) "
                    + "OR (kind = 'CreditNote' AND total_net_minor < 0 AND total_vat_minor <= 0 AND referenced_invoice_id IS NOT NULL "
                    + "AND referenced_invoice_number IS NOT NULL)");
                // The printed number is derived from series, year and sequence; the year is the issue date's year.
                t.HasCheckConstraint("ck_invoices_number",
                    "sequence_number BETWEEN 1 AND 999999 AND number_year = extract(year FROM issue_date) "
                    + "AND number = (CASE kind WHEN 'Invoice' THEN 'HN-' ELSE 'HN-RK-' END) || lpad(number_year::text, 4, '0') || '-' "
                    + "|| lpad(sequence_number::text, 6, '0')");
                t.HasCheckConstraint("ck_invoices_period",
                    "service_period_end >= service_period_start AND extract(day FROM billing_month) = 1");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Number).HasMaxLength(30).IsRequired();
            b.Property(x => x.ReferencedInvoiceNumber).HasMaxLength(30);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Issuer).HasJsonConversion().IsRequired();
            b.Property(x => x.Recipient).HasJsonConversion().IsRequired();
            b.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.Kind, x.NumberYear, x.SequenceNumber }).IsUnique();
            // One commission invoice per seller and month; one correction per invoice and refund month.
            b.HasIndex(x => new { x.OrganizationId, x.BillingMonth }).IsUnique().HasFilter("kind = 'Invoice'")
                .HasDatabaseName("ux_invoices_one_per_seller_month");
            b.HasIndex(x => new { x.ReferencedInvoiceId, x.BillingMonth }).IsUnique().HasFilter("kind = 'CreditNote'")
                .HasDatabaseName("ux_invoices_one_credit_note_per_invoice_month");
            b.HasIndex(x => new { x.BillingMonth, x.Kind });
            b.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReferencedInvoiceId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => new { l.OrganizationId, l.InvoiceId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<InvoiceLine>(b =>
        {
            b.ToTable("invoice_lines", Schema, t =>
            {
                t.HasCheckConstraint("ck_invoice_lines_amounts", "gross_minor = net_minor + vat_minor");
                t.HasCheckConstraint("ck_invoice_lines_kind",
                    "(kind = 'Invoice' AND net_minor > 0 AND vat_minor >= 0 AND credited_invoice_id IS NULL AND credited_line_number IS NULL) "
                    + "OR (kind = 'CreditNote' AND net_minor < 0 AND vat_minor <= 0 AND credited_invoice_id IS NOT NULL AND credited_line_number IS NOT NULL)");
            });
            b.HasKey(x => new { x.InvoiceId, x.LineNumber });
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired();
            // A payment is invoiced at most once and credited at most once.
            b.HasIndex(x => new { x.PaymentId, x.Kind }).IsUnique();
            b.HasIndex(x => x.OrganizationId);
            b.HasOne<InvoiceLine>().WithMany().HasForeignKey(x => new { x.CreditedInvoiceId, x.CreditedLineNumber })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
