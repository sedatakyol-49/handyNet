using FluentValidation;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

public sealed record CreateRegisterRequest(string Name, Guid? WarehouseId);

public sealed record UpdateRegisterRequest(string Name, bool IsActive);

public sealed record RegisterDto(Guid Id, string Name, Guid WarehouseId, bool IsActive, long NextReceiptNumber, Guid? OpenSessionId, DateTimeOffset CreatedAt);

public sealed record OpenSessionRequest(long OpeningCashMinor);

public sealed record CashMovementRequest(CashMovementKind Kind, long AmountMinor, string Reason);

public sealed record CloseSessionRequest(long CountedCashMinor, string? Note);

public sealed record CashMovementDto(Guid Id, string Kind, long AmountMinor, string Reason, DateTimeOffset CreatedAt, Guid CreatedBy);

/// <summary>For an open session the summary and expected cash are live figures; for a closed one, the stored Z-report.</summary>
public sealed record SessionDto(
    Guid Id,
    Guid RegisterId,
    string RegisterName,
    string Status,
    DateTimeOffset OpenedAt,
    Guid OpenedBy,
    long OpeningCashMinor,
    DateTimeOffset? ClosedAt,
    Guid? ClosedBy,
    long ExpectedCashMinor,
    long? CountedCashMinor,
    long? DifferenceMinor,
    string? ClosingNote,
    SessionSummary Summary,
    IReadOnlyList<CashMovementDto> Movements);

internal static class PosLimits
{
    /// <summary>Plausibility cap for single amounts (1 million euro).</summary>
    public const long MaxAmountMinor = 100_000_000;
}

internal sealed class CreateRegisterValidator : AbstractValidator<CreateRegisterRequest>
{
    public CreateRegisterValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Register.MaxNameLength);
}

internal sealed class UpdateRegisterValidator : AbstractValidator<UpdateRegisterRequest>
{
    public UpdateRegisterValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Register.MaxNameLength);
}

internal sealed class OpenSessionValidator : AbstractValidator<OpenSessionRequest>
{
    public OpenSessionValidator() => RuleFor(x => x.OpeningCashMinor).InclusiveBetween(0, PosLimits.MaxAmountMinor);
}

internal sealed class CashMovementValidator : AbstractValidator<CashMovementRequest>
{
    public CashMovementValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.AmountMinor).InclusiveBetween(1, PosLimits.MaxAmountMinor);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(CashMovement.MaxReasonLength);
    }
}

internal sealed class CloseSessionValidator : AbstractValidator<CloseSessionRequest>
{
    public CloseSessionValidator()
    {
        RuleFor(x => x.CountedCashMinor).InclusiveBetween(0, PosLimits.MaxAmountMinor);
        RuleFor(x => x.Note).MaximumLength(CashSession.MaxNoteLength);
    }
}

/// <summary>Row locks and the gapless receipt number allocation (raw SQL; not composable, so always materialized with ToListAsync).</summary>
internal static class PosSql
{
    /// <summary>
    /// Takes the register's next receipt number and increments it in one row-locking UPDATE. Must run inside the
    /// transaction that inserts the receipt: concurrent sales on the same register wait here, and a rollback returns
    /// the number. Null if the register is not the tenant's or is inactive.
    /// </summary>
    public static async Task<long?> AllocateReceiptNumberAsync(HandyNetDbContext db, Guid registerId, CancellationToken ct) =>
        (await db.Database.SqlQuery<long>($"""
                UPDATE pos.registers SET next_receipt_number = next_receipt_number + 1
                WHERE id = {registerId} AND is_active
                RETURNING next_receipt_number - 1 AS "Value"
                """).ToListAsync(ct))
        .Cast<long?>().FirstOrDefault();

    /// <summary>
    /// Locks the register's open session: shared for sales (many may run, a close waits for them), exclusive for cash
    /// movements, refunds and the closing (expected cash must not change while they decide).
    /// </summary>
    public static async Task<Guid?> LockOpenSessionAsync(HandyNetDbContext db, Guid registerId, bool exclusive, CancellationToken ct)
    {
        var rows = exclusive
            ? await db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM pos.cash_sessions WHERE register_id = {registerId} AND status = 'Open' FOR UPDATE").ToListAsync(ct)
            : await db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM pos.cash_sessions WHERE register_id = {registerId} AND status = 'Open' FOR SHARE").ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public static async Task<long> ExpectedCashAsync(HandyNetDbContext db, CashSession session, CancellationToken ct)
    {
        var cash = await (from p in db.Set<ReceiptPayment>()
                          join r in db.Set<Receipt>() on p.ReceiptId equals r.Id
                          where r.SessionId == session.Id && p.Method == PaymentMethod.Cash
                          select p.AmountMinor).SumAsync(ct);
        var movements = await db.Set<CashMovement>().Where(m => m.SessionId == session.Id)
            .Select(m => m.Kind == CashMovementKind.Deposit ? m.AmountMinor : -m.AmountMinor).SumAsync(ct);
        return session.OpeningCashMinor + cash + movements;
    }
}

internal sealed class RegisterService(HandyNetDbContext db, ITenantContext tenant, IInventory inventory, IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<IReadOnlyList<RegisterDto>> ListAsync(CancellationToken ct)
    {
        var registers = await db.Set<Register>().AsNoTracking().Where(r => r.OrganizationId == OrganizationId).OrderBy(r => r.Name).ToListAsync(ct);
        var open = await OpenSessionsAsync(registers.Select(r => r.Id).ToList(), ct);
        return registers.Select(r => ToDto(r, open.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<Result<RegisterDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        return ToDto(register, (await OpenSessionsAsync([id], ct)).GetValueOrDefault(id));
    }

    public async Task<Result<RegisterDto>> CreateAsync(CreateRegisterRequest request, CancellationToken ct)
    {
        var warehouseId = request.WarehouseId ?? await inventory.GetOrStageDefaultWarehouseAsync(ct);
        if (request.WarehouseId is { } requested && !await inventory.IsOwnWarehouseAsync(requested, ct))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");

        var register = Register.Create(OrganizationId, request.Name, warehouseId, clock.UtcNow);
        if (!register.IsSuccess)
            return register.Error!;
        db.Add(register.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("pos.register_name_taken", "A register with this name already exists.");
        }
        return ToDto(register.Value, null);
    }

    public async Task<Result<RegisterDto>> UpdateAsync(Guid id, UpdateRegisterRequest request, CancellationToken ct)
    {
        var register = await db.Set<Register>().FirstOrDefaultAsync(r => r.Id == id && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var openSession = (await OpenSessionsAsync([id], ct)).GetValueOrDefault(id);
        var updated = register.Update(request.Name, request.IsActive, openSession is not null);
        if (!updated.IsSuccess)
            return updated.Error!;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("pos.register_name_taken", "A register with this name already exists.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The register was changed concurrently. Reload and try again.");
        }
        // NextReceiptNumber may have moved since the entity was loaded; re-read for the response.
        return await GetAsync(id, ct);
    }

    private async Task<Dictionary<Guid, Guid?>> OpenSessionsAsync(IReadOnlyCollection<Guid> registerIds, CancellationToken ct) =>
        await db.Set<CashSession>().AsNoTracking()
            .Where(s => registerIds.Contains(s.RegisterId) && s.Status == CashSessionStatus.Open)
            .ToDictionaryAsync(s => s.RegisterId, s => (Guid?)s.Id, ct);

    private static RegisterDto ToDto(Register r, Guid? openSessionId) =>
        new(r.Id, r.Name, r.WarehouseId, r.IsActive, r.NextReceiptNumber, openSessionId, r.CreatedAt);
}

internal sealed class SessionService(HandyNetDbContext db, ITenantContext tenant, ICurrentUser currentUser, IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<SessionDto>> OpenAsync(Guid registerId, OpenSessionRequest request, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (await db.Set<CashSession>().AnyAsync(s => s.RegisterId == registerId && s.Status == CashSessionStatus.Open, ct))
            return Error.Conflict("pos.session_already_open", "This register already has an open cash session.");

        var session = CashSession.Open(OrganizationId, register, request.OpeningCashMinor, currentUser.UserId, clock.UtcNow);
        if (!session.IsSuccess)
            return session.Error!;
        db.Add(session.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_cash_sessions_one_open_per_register"))
        {
            return Error.Conflict("pos.session_already_open", "This register already has an open cash session.");
        }
        return await ToDtoAsync(session.Value, register.Name, ct);
    }

    public async Task<Result<SessionDto>> GetCurrentAsync(Guid registerId, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var session = await db.Set<CashSession>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.RegisterId == registerId && s.Status == CashSessionStatus.Open, ct);
        if (session is null)
            return Error.NotFound("pos.no_open_session", "This register has no open cash session.");
        return await ToDtoAsync(session, register.Name, ct);
    }

    public async Task<Result<SessionDto>> GetAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Set<CashSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.OrganizationId == OrganizationId, ct);
        if (session is null)
            return Error.NotFound("pos.session_not_found", "Cash session not found.");
        return await ToDtoAsync(session, await RegisterNameAsync(session.RegisterId, ct), ct);
    }

    public async Task<PagedResult<SessionDto>> ListAsync(Guid? registerId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<CashSession>().AsNoTracking().Where(s => s.OrganizationId == OrganizationId);
        if (registerId is { } id)
            query = query.Where(s => s.RegisterId == id);
        var total = await query.CountAsync(ct);
        var sessions = await query.OrderByDescending(s => s.OpenedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await db.Set<Register>().AsNoTracking().Where(r => r.OrganizationId == OrganizationId).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var items = new List<SessionDto>();
        foreach (var session in sessions)
            items.Add(await ToDtoAsync(session, names.GetValueOrDefault(session.RegisterId, "?"), ct));
        return new PagedResult<SessionDto>(items, p, size, total);
    }

    public async Task<Result<CashMovementDto>> AddMovementAsync(Guid sessionId, CashMovementRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await db.Set<CashSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.OrganizationId == OrganizationId, ct);
        if (session is null)
            return Error.NotFound("pos.session_not_found", "Cash session not found.");
        if (await PosSql.LockOpenSessionAsync(db, session.RegisterId, exclusive: true, ct) != session.Id)
            return Error.Conflict("pos.session_closed", "The cash session is closed.");

        var expected = await PosSql.ExpectedCashAsync(db, session, ct);
        var movement = CashMovement.Record(session, request.Kind, request.AmountMinor, request.Reason, expected, currentUser.UserId, clock.UtcNow);
        if (!movement.IsSuccess)
            return movement.Error!;
        db.Add(movement.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(movement.Value);
    }

    /// <summary>
    /// Tagesabschluss: locks the session exclusively (waits for running sales), computes the Z-report from the
    /// session's receipts and cash movements, and stores expected cash, counted cash and the difference.
    /// </summary>
    public async Task<Result<SessionDto>> CloseAsync(Guid sessionId, CloseSessionRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var registerId = await db.Set<CashSession>().AsNoTracking().Where(s => s.Id == sessionId && s.OrganizationId == OrganizationId)
            .Select(s => (Guid?)s.RegisterId).FirstOrDefaultAsync(ct);
        if (registerId is null)
            return Error.NotFound("pos.session_not_found", "Cash session not found.");
        if (await PosSql.LockOpenSessionAsync(db, registerId.Value, exclusive: true, ct) != sessionId)
            return Error.Conflict("pos.session_closed", "The cash session is already closed.");

        var session = await db.Set<CashSession>().FirstAsync(s => s.Id == sessionId, ct);
        var summary = await SummarizeAsync(session, ct);
        var closed = session.Close(summary, request.CountedCashMinor, request.Note, currentUser.UserId, clock.UtcNow);
        if (!closed.IsSuccess)
            return closed.Error!;
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The session was changed concurrently. Reload and try again.");
        }
        return await ToDtoAsync(session, await RegisterNameAsync(session.RegisterId, ct), ct);
    }

    private async Task<SessionSummary> SummarizeAsync(CashSession session, CancellationToken ct)
    {
        var receipts = await db.Set<Receipt>().AsNoTracking().Include(r => r.Lines).Include(r => r.Payments)
            .Where(r => r.SessionId == session.Id).AsSplitQuery().ToListAsync(ct);
        var movements = await db.Set<CashMovement>().AsNoTracking().Where(m => m.SessionId == session.Id).ToListAsync(ct);
        return SessionCalculator.Summarize(session.OpeningCashMinor, receipts, movements);
    }

    private async Task<string> RegisterNameAsync(Guid registerId, CancellationToken ct) =>
        await db.Set<Register>().AsNoTracking().Where(r => r.Id == registerId).Select(r => r.Name).FirstAsync(ct);

    private async Task<SessionDto> ToDtoAsync(CashSession s, string registerName, CancellationToken ct)
    {
        var summary = s.Summary ?? await SummarizeAsync(s, ct);
        var movements = await db.Set<CashMovement>().AsNoTracking().Where(m => m.SessionId == s.Id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return new SessionDto(s.Id, s.RegisterId, registerName, s.Status.ToString(), s.OpenedAt, s.OpenedBy, s.OpeningCashMinor, s.ClosedAt, s.ClosedBy,
            s.ExpectedCashMinor ?? summary.ExpectedCashMinor, s.CountedCashMinor, s.DifferenceMinor, s.ClosingNote, summary,
            movements.Select(ToDto).ToList());
    }

    private static CashMovementDto ToDto(CashMovement m) => new(m.Id, m.Kind.ToString(), m.AmountMinor, m.Reason, m.CreatedAt, m.CreatedBy);
}
