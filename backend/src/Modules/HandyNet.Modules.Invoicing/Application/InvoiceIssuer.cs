using HandyNet.Modules.Invoicing.Domain;
using HandyNet.Modules.Payments.Contracts;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Invoicing.Application;

internal static class InvoiceNumbers
{
    /// <summary>
    /// Takes the next number of the series for the year and advances the platform-wide counter in one row-locking
    /// statement. Must run inside the transaction that inserts the document: concurrent issuers wait here, and a
    /// rollback returns the number. The database refuses a commit that took a number without using it (migration).
    /// </summary>
    public static async Task<long> AllocateAsync(HandyNetDbContext db, InvoiceKind kind, int year, CancellationToken ct) =>
        (await db.Database.SqlQuery<long>($"""
                INSERT INTO invoicing.number_sequences (kind, year, next_number) VALUES ({kind.ToString()}, {year}, 2)
                ON CONFLICT (kind, year) DO UPDATE SET next_number = invoicing.number_sequences.next_number + 1
                RETURNING next_number - 1 AS "Value"
                """).ToListAsync(ct))
        .Single();
}

/// <summary>What issuing one month produced for one seller.</summary>
internal sealed record SellerIssueOutcome(IReadOnlyList<Invoice> Created, IReadOnlyList<Invoice> Existing);

/// <summary>
/// Issues the commission invoice and the credit notes of one month for the current tenant (the seller), normally as
/// its system actor. Idempotent: an existing invoice for (seller, month) and an existing credit note for
/// (invoice, refund month) are returned instead of issuing again; the unique indexes catch concurrent runs.
/// Each document is written in its own transaction together with its number.
/// </summary>
internal sealed class InvoiceIssuer(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICommissionStatements payments,
    IOrganizationDirectory organizations,
    IOptions<InvoicingOptions> options,
    IHostEnvironment environment,
    IClock clock)
{
    private static TimeZoneInfo Zone => InvoiceCalendar.GermanTimeZone;

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<SellerIssueOutcome>> IssueMonthAsync(BillingMonth month, CancellationToken ct)
    {
        if (!month.IsOver(clock.UtcNow, Zone))
            return Error.Validation("invoicing.month_not_over", $"{month} is not over yet; invoices are issued for completed months only.");
        var issuer = Issuer();
        if (!issuer.IsSuccess)
            return issuer.Error!;

        var created = new List<Invoice>();
        var existing = new List<Invoice>();

        var invoice = await IssueInvoiceAsync(month, issuer.Value, ct);
        if (!invoice.IsSuccess)
            return invoice.Error!;
        if (invoice.Value is { } inv)
            (inv.Created ? created : existing).Add(inv.Invoice);

        var notes = await IssueCreditNotesAsync(month, issuer.Value, ct);
        if (!notes.IsSuccess)
            return notes.Error!;
        foreach (var note in notes.Value)
            (note.Created ? created : existing).Add(note.Invoice);
        return new SellerIssueOutcome(created, existing);
    }

    public Result<InvoiceParty> Issuer()
    {
        var party = options.Value.Issuer.ToParty();
        var valid = party.ValidateAsIssuer(environment.IsProduction());
        return valid.IsSuccess ? party : valid.Error!;
    }

    private async Task<Result<(Invoice Invoice, bool Created)?>> IssueInvoiceAsync(BillingMonth month, InvoiceParty issuer, CancellationToken ct)
    {
        if (await FindInvoiceAsync(month, ct) is { } done)
            return (done, false);

        var items = (await payments.GetCapturedAsync(month.Start(Zone), month.End(Zone), ct))
            .Where(c => c.CommissionNetMinor != 0 || c.CommissionVatMinor != 0)
            .Select(c => new CommissionItem(c.PaymentId, c.OrderId, c.OrderNumber, c.PaidAt, c.CommissionNetMinor, c.CommissionVatMinor, c.Currency))
            .ToList();
        if (items.Count == 0)
            return ((Invoice, bool)?)null;
        var recipient = await RecipientAsync(ct);
        if (!recipient.IsSuccess)
            return recipient.Error!;

        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sequence = await InvoiceNumbers.AllocateAsync(db, InvoiceKind.Invoice, InvoiceCalendar.LocalDate(now, Zone).Year, ct);
        // Holding the counter lock now: a concurrent run for the same seller has either committed (seen here) or waits.
        if (await FindInvoiceAsync(month, ct) is { } concurrent)
            return (concurrent, false); // disposing the transaction rolls the number back

        var number = InvoiceNumber.Create(InvoiceKind.Invoice, InvoiceCalendar.LocalDate(now, Zone).Year, sequence);
        if (!number.IsSuccess)
            return number.Error!;
        var invoice = Invoice.IssueCommissionInvoice(OrganizationId, month, number.Value, issuer, recipient.Value, items,
            options.Value.VatRateBasisPoints, now, Zone);
        if (!invoice.IsSuccess)
            return invoice.Error!;

        db.Add(invoice.Value);
        if (!await CommitAsync(tx, ct))
            return await FindInvoiceAsync(month, ct) is { } winner ? (winner, false) : Error.Conflict("invoicing.concurrent", "Concurrent issuing; retry.");
        return (invoice.Value, true);
    }

    private async Task<Result<List<(Invoice Invoice, bool Created)>>> IssueCreditNotesAsync(BillingMonth month, InvoiceParty issuer, CancellationToken ct)
    {
        var result = new List<(Invoice, bool)>();
        var billingMonth = month.FirstDay;
        var existing = await Documents()
            .Where(i => i.OrganizationId == OrganizationId && i.Kind == InvoiceKind.CreditNote && i.BillingMonth == billingMonth)
            .ToListAsync(ct);
        result.AddRange(existing.Select(e => (e, false)));

        var refunded = (await payments.GetRefundedAsync(month.Start(Zone), month.End(Zone), ct)).Select(r => r.PaymentId).ToList();
        if (refunded.Count == 0)
            return result;

        // Refunded payments that were invoiced and not yet credited, grouped by their invoice.
        var credited = await db.Set<InvoiceLine>().AsNoTracking()
            .Where(l => l.OrganizationId == OrganizationId && l.Kind == InvoiceKind.CreditNote && refunded.Contains(l.PaymentId))
            .Select(l => l.PaymentId).ToListAsync(ct);
        var open = await db.Set<InvoiceLine>().AsNoTracking()
            .Where(l => l.OrganizationId == OrganizationId && l.Kind == InvoiceKind.Invoice && refunded.Contains(l.PaymentId) && !credited.Contains(l.PaymentId))
            .ToListAsync(ct);
        if (open.Count == 0)
            return result;

        var recipient = await RecipientAsync(ct);
        if (!recipient.IsSuccess)
            return recipient.Error!;

        foreach (var group in open.GroupBy(l => l.InvoiceId).OrderBy(g => g.Key))
        {
            if (existing.Any(e => e.ReferencedInvoiceId == group.Key))
                continue; // this invoice was already corrected for this month

            var now = clock.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var sequence = await InvoiceNumbers.AllocateAsync(db, InvoiceKind.CreditNote, InvoiceCalendar.LocalDate(now, Zone).Year, ct);
            if (await FindCreditNoteAsync(group.Key, billingMonth, ct) is { } concurrent)
            {
                result.Add((concurrent, false));
                continue;
            }

            var original = await Documents().FirstAsync(i => i.Id == group.Key && i.OrganizationId == OrganizationId, ct);
            var number = InvoiceNumber.Create(InvoiceKind.CreditNote, InvoiceCalendar.LocalDate(now, Zone).Year, sequence);
            if (!number.IsSuccess)
                return number.Error!;
            var note = Invoice.IssueCreditNote(original, group.Select(l => l.PaymentId).ToList(), month, number.Value, issuer, recipient.Value, now, Zone);
            if (!note.IsSuccess)
                return note.Error!;

            db.Add(note.Value);
            if (await CommitAsync(tx, ct))
                result.Add((note.Value, true));
            else if (await FindCreditNoteAsync(group.Key, billingMonth, ct) is { } winner)
                result.Add((winner, false));
            else
                return Error.Conflict("invoicing.concurrent", "Concurrent issuing; retry.");
        }
        return result;
    }

    private async Task<bool> CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // A concurrent run issued the same document first; ours is rolled back together with its number.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private IQueryable<Invoice> Documents() => db.Set<Invoice>().AsNoTracking().Include(i => i.Lines);

    private Task<Invoice?> FindInvoiceAsync(BillingMonth month, CancellationToken ct)
    {
        var first = month.FirstDay;
        return Documents().FirstOrDefaultAsync(i => i.OrganizationId == OrganizationId && i.Kind == InvoiceKind.Invoice && i.BillingMonth == first, ct);
    }

    private Task<Invoice?> FindCreditNoteAsync(Guid invoiceId, DateOnly billingMonth, CancellationToken ct) =>
        Documents().FirstOrDefaultAsync(i => i.OrganizationId == OrganizationId && i.Kind == InvoiceKind.CreditNote
                                             && i.ReferencedInvoiceId == invoiceId && i.BillingMonth == billingMonth, ct);

    private async Task<Result<InvoiceParty>> RecipientAsync(CancellationToken ct)
    {
        var details = await organizations.GetLegalDetailsAsync(OrganizationId, ct);
        if (details is null)
            return Error.NotFound("organization.not_found", "Organization not found.");
        return new InvoiceParty(details.Name, details.LegalName, details.Street, details.PostalCode, details.City, details.CountryCode, details.VatId, null);
    }
}
