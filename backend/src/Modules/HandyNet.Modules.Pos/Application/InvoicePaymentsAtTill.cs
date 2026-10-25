using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>"An der Kasse kassieren": a sales invoice paid in the shop (cash or card) goes through the till.</summary>
internal sealed partial class SaleService
{
    /// <summary>
    /// One transaction, like a sale: receipt number (register row lock) → open session (shared lock) → invoice row lock → open
    /// amount → InvoicePayment receipt (one not taxable line, no stock: the invoice already moved it) → TSE signature → the
    /// invoice payment referencing the receipt. Requires an Idempotency-Key; a retry returns the same receipt.
    /// </summary>
    public async Task<Result<CollectInvoiceResult>> CollectInvoiceAsync(Guid invoiceId, CollectInvoiceRequest request, string idempotencyKey,
        SalesInvoiceService invoices, CancellationToken ct)
    {
        var requestHash = HashCollect(invoiceId, request);
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } replay)
            return replay.Map(r => new CollectInvoiceResult(r.Receipt, invoiceId, Replayed: true));
        if (await fiscal.CheckAsync(request.RegisterId, ct) is { } fiscalRefused)
            return fiscalRefused;

        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RegisterId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (actor.CheckRegister(register.Id) is { } otherRegister)
            return otherRegister;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await PosSql.AllocateReceiptNumberAsync(db, register.Id, ct) is not { } number)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } concurrentReplay)
            return concurrentReplay.Map(r => new CollectInvoiceResult(r.Receipt, invoiceId, Replayed: true));
        if (await PosSql.LockOpenSessionAsync(db, register.Id, exclusive: false, ct) is not { } sessionId)
            return Error.Conflict("pos.no_open_session", "Open a cash session on this register first.");

        var invoice = await invoices.LockAsync(invoiceId, ct);
        if (invoice is null)
            return Error.NotFound("pos.sales_invoice_not_found", "Invoice not found.");
        if (invoice.Kind != SalesInvoiceKind.Invoice || invoice.Status != SalesInvoiceStatus.Issued)
            return Error.Conflict("pos.sales_invoice_not_issued", "Only issued invoices can be paid.");
        var (paid, cancelled) = await invoices.PaymentStateAsync(invoiceId, ct);
        if (cancelled)
            return Error.Conflict("pos.sales_invoice_cancelled", "The invoice was cancelled with a credit note.");
        var open = invoice.TotalGrossMinor - paid;
        if (open <= 0)
            return Error.Conflict("pos.sales_invoice_paid", "The invoice is already paid.");
        var amount = request.AmountMinor ?? open;
        if (amount > open)
            return Error.Conflict("pos.payment_exceeds_open", $"Only {open / 100m:0.00} € are open on this invoice.")
                .WithDetails(new Dictionary<string, object?> { ["openMinor"] = open });

        var customer = invoice.CustomerId is { } customerId
            ? (await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct))?.ToDocument()
            : null;
        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, register.WarehouseId, number, actor.UserId, clock.UtcNow, idempotencyKey,
            requestHash, customer);
        var receipt = Receipt.CreateInvoicePayment(header, invoice.Id, invoice.Number!, amount,
            request.Payments.Select(p => new PaymentInput(p.Method, p.AmountMinor)).ToList(), register.CashRounding);
        if (!receipt.IsSuccess)
            return receipt.Error!;
        var sealedResult = await SealAsync(receipt.Value, register.Name, null, ct);
        if (!sealedResult.IsSuccess)
            return sealedResult.Error!;

        var method = receipt.Value.Payments.Any(p => p.Method == PaymentMethod.Cash) ? InvoicePaymentMethod.Cash : InvoicePaymentMethod.Card;
        var payment = SalesInvoicePayment.Record(invoice, method, amount, BerlinTime.Today(clock.UtcNow), $"Kassenbeleg Nr. {number} ({register.Name})", paid,
            cancelled, receipt.Value.Id, actor.UserId, clock.UtcNow);
        if (!payment.IsSuccess)
            return payment.Error!;

        db.Add(receipt.Value);
        db.Add(payment.Value);
        try
        {
            await db.SaveChangesAsync(ct);
            if (await approvals.ConsumeAsync(actor, ApprovalUse.Sale, receipt.Value.Id, null, null, ct) is { } approvalError)
                return approvalError;
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ix_receipts_organization_id_idempotency_key"))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return (await ReplayAsync(idempotencyKey, requestHash, ct))?.Map(r => new CollectInvoiceResult(r.Receipt, invoiceId, Replayed: true))
                   ?? Error.Conflict("pos.duplicate", "The payment could not be recorded. Try again.");
        }
        return new CollectInvoiceResult(await ToDtoAsync(receipt.Value, ct), invoiceId, Replayed: false);
    }

    internal static string HashCollect(Guid invoiceId, CollectInvoiceRequest request)
    {
        var canonical = $"collect#{invoiceId:N}#{request.RegisterId:N}#{request.AmountMinor}#"
                        + string.Join("|", request.Payments.OrderBy(p => p.Method).ThenBy(p => p.AmountMinor).Select(p => $"{p.Method}:{p.AmountMinor}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
