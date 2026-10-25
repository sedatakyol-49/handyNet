using HandyNet.Modules.Pos.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// <see cref="ICashPayouts"/>: an Ankauf payout is a cash withdrawal with the system reason <c>Ankauf</c>, recorded in the
/// caller's transaction under an exclusive lock of the register's open session (like a manual withdrawal), signed as
/// <c>SonstigerVorgang</c>. The acting user is the signed-in user (cashier tokens are only resolved on POS endpoints).
/// </summary>
internal sealed class CashPayouts(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IClock clock, FiscalSealer fiscal) : ICashPayouts
{
    public bool IsAvailable => tenant.HasModule(PlatformModule.Pos);

    public async Task<IReadOnlyList<PayoutRegister>> ListRegistersAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            return [];
        var org = tenant.RequiredOrganizationId;
        var registers = await db.Set<Register>().AsNoTracking().Where(r => r.OrganizationId == org && r.IsActive).OrderBy(r => r.Name).ToListAsync(ct);
        var ids = registers.Select(r => r.Id).ToList();
        var sessions = await db.Set<CashSession>().AsNoTracking()
            .Where(s => ids.Contains(s.RegisterId) && s.Status == CashSessionStatus.Open).ToListAsync(ct);
        var result = new List<PayoutRegister>();
        foreach (var register in registers)
        {
            var session = sessions.FirstOrDefault(s => s.RegisterId == register.Id);
            result.Add(new PayoutRegister(register.Id, register.Name, session?.Id,
                session is null ? null : await PosSql.ExpectedCashAsync(db, session, ct)));
        }
        return result;
    }

    public async Task<Result<CashPayoutResult>> PayOutAsync(CashPayoutRequest request, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A cash payout must run inside the caller's transaction.");
        if (!IsAvailable)
            return Error.Forbidden("module.disabled", $"Module '{PlatformModule.Pos}' is not enabled for this organization.");
        if (!actor.Has(Permissions.PosCashMovement))
            return Error.Forbidden("permission.denied", $"Missing permission '{Permissions.PosCashMovement}'.");
        var org = tenant.RequiredOrganizationId;
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RegisterId && r.OrganizationId == org, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (!register.IsActive)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");

        var sessionId = await PosSql.LockOpenSessionAsync(db, register.Id, exclusive: true, ct);
        if (sessionId is null)
            return Error.Conflict("pos.no_open_session", "Open a cash session on this register first.");
        var session = await db.Set<CashSession>().AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
        var expected = await PosSql.ExpectedCashAsync(db, session, ct);
        var reason = request.Kind switch
        {
            CashPayoutKind.Ankauf => CashMovementReason.Ankauf,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var movement = CashMovement.RecordPayout(session, reason, request.AmountMinor, request.Description, request.Reference, expected, actor.UserId,
            clock.UtcNow);
        if (!movement.IsSuccess)
            return movement.Error!;
        if (await db.Set<CashMovement>().AnyAsync(m => m.OrganizationId == org && m.SourceReference == request.Reference, ct))
            return Error.Conflict("pos.payout_exists", "This purchase was already paid out of a till.");

        await fiscal.SignOtherAsync(register.Id, session.Id, FiscalRecordKind.CashMovement, movement.Value.Id,
            KassenSichV.OtherProcessData("Entnahme", reason.ToString(), movement.Value.AmountMinor), ct);
        db.Add(movement.Value);
        return new CashPayoutResult(movement.Value.Id, session.Id, register.Id, register.Name);
    }
}

/// <summary><see cref="IPosDailyFigures"/> under the caller's RLS (own organization only).</summary>
internal sealed class PosDailyFigures(HandyNetDbContext db, ITenantContext tenant) : IPosDailyFigures
{
    public async Task<PosDayFigures> GetAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var org = tenant.RequiredOrganizationId;
        var fromUtc = from.ToUniversalTime();
        var untilUtc = until.ToUniversalTime();
        var all = await db.Set<Receipt>().AsNoTracking().Include(r => r.Lines).Include(r => r.Payments)
            .Where(r => r.OrganizationId == org && r.CreatedAt >= fromUtc && r.CreatedAt < untilUtc).AsSplitQuery().ToListAsync(ct);
        var receipts = all.Where(r => r.Kind != ReceiptKind.InvoicePayment).ToList();
        var invoicePayments = all.Where(r => r.Kind == ReceiptKind.InvoicePayment).ToList();
        var salesInvoices = await db.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.IssuedAt >= fromUtc && i.IssuedAt < untilUtc)
            .Select(i => new { i.Kind, i.TotalGrossMinor }).ToListAsync(ct);
        var movements = await (from m in db.Set<CashMovement>().AsNoTracking()
                               where m.OrganizationId == org && m.CreatedAt >= fromUtc && m.CreatedAt < untilUtc
                               select m).ToListAsync(ct);
        var invoices = await db.Set<PosInvoice>().AsNoTracking().CountAsync(i => i.OrganizationId == org && i.IssuedAt >= fromUtc && i.IssuedAt < untilUtc, ct);

        // Sessions that were open at any time during the period.
        var sessions = await (from s in db.Set<CashSession>().AsNoTracking()
                              join r in db.Set<Register>().AsNoTracking() on s.RegisterId equals r.Id
                              where s.OrganizationId == org && s.OpenedAt < untilUtc && (s.ClosedAt == null || s.ClosedAt >= fromUtc)
                              orderby r.Name, s.OpenedAt
                              select new { Session = s, RegisterName = r.Name }).ToListAsync(ct);
        var sessionFigures = new List<PosSessionFigure>();
        foreach (var x in sessions)
        {
            var s = x.Session;
            var expected = s.ExpectedCashMinor ?? await PosSql.ExpectedCashAsync(db, s, ct);
            sessionFigures.Add(new PosSessionFigure(s.Id, x.RegisterName, s.Status.ToString(), s.OpenedAt, s.ClosedAt, expected, s.CountedCashMinor,
                s.DifferenceMinor));
        }

        var lines = receipts.SelectMany(r => r.Lines).ToList();
        var payments = receipts.SelectMany(r => r.Payments).ToList();
        var ankauf = movements.Where(m => m.ReasonCode == CashMovementReason.Ankauf).ToList();
        var gross = receipts.Sum(r => r.TotalGrossMinor);
        var vat = receipts.Sum(r => r.TotalVatMinor);
        return new PosDayFigures(
            receipts.Count(r => r.Kind == ReceiptKind.Sale),
            receipts.Count(r => r.Kind == ReceiptKind.Storno),
            receipts.Count(r => r.Kind == ReceiptKind.Return),
            receipts.Where(r => r.Kind == ReceiptKind.Sale).Sum(r => r.TotalGrossMinor),
            receipts.Where(r => r.Kind is ReceiptKind.Storno or ReceiptKind.Return).Sum(r => r.TotalGrossMinor),
            gross,
            gross - vat,
            vat,
            lines.GroupBy(l => (l.TaxTreatment, l.VatRateBasisPoints)).OrderBy(g => g.Key.TaxTreatment).ThenByDescending(g => g.Key.VatRateBasisPoints)
                .Select(g => new PosTaxFigure(g.Key.TaxTreatment.ToString(), g.Key.VatRateBasisPoints, g.Sum(l => l.LineGrossMinor), g.Sum(l => l.NetMinor),
                    g.Sum(l => l.VatMinor)))
                .ToList(),
            Enum.GetValues<PaymentMethod>()
                .Select(m => new PosPaymentFigure(m.ToString(), payments.Count(p => p.Method == m), payments.Where(p => p.Method == m).Sum(p => p.AmountMinor)))
                .ToList(),
            movements.Where(m => m.Kind == CashMovementKind.Deposit).Sum(m => m.AmountMinor),
            movements.Where(m => m.Kind == CashMovementKind.Withdrawal).Sum(m => m.AmountMinor),
            ankauf.Count,
            ankauf.Sum(m => m.AmountMinor),
            invoices,
            sessionFigures,
            invoicePayments.Count,
            invoicePayments.Sum(r => r.TotalGrossMinor),
            salesInvoices.Count(i => i.Kind == SalesInvoiceKind.Invoice),
            salesInvoices.Sum(i => i.TotalGrossMinor));
    }
}
