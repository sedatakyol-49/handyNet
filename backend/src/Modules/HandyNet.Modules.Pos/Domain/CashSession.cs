using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>Stored as text.</summary>
public enum CashSessionStatus
{
    Open,
    Closed,
}

/// <summary>Stored as text.</summary>
public enum CashMovementKind
{
    /// <summary>Einlage: cash put into the drawer (e.g. change money).</summary>
    Deposit,

    /// <summary>Entnahme: cash taken out (e.g. to the bank).</summary>
    Withdrawal,
}

public sealed record PaymentTotal(PaymentMethod Method, long AmountMinor);

public sealed record TaxTotal(TaxTreatment Treatment, int VatRateBasisPoints, long GrossMinor, long NetMinor, long VatMinor);

/// <summary>Z-report figures of a cash session. Stored with the session when it is closed and never changed afterwards.</summary>
public sealed record SessionSummary(
    int ReceiptCount,
    int SaleCount,
    int StornoCount,
    int ReturnCount,
    long TotalGrossMinor,
    long TotalNetMinor,
    long TotalVatMinor,
    IReadOnlyList<PaymentTotal> ByPaymentMethod,
    IReadOnlyList<TaxTotal> ByTaxTreatment,
    long OpeningCashMinor,
    long CashPaymentsMinor,
    long DepositsMinor,
    long WithdrawalsMinor,
    long ExpectedCashMinor,
    long? FirstReceiptNumber,
    long? LastReceiptNumber);

public static class SessionCalculator
{
    /// <summary>
    /// Expected cash in the drawer = opening cash + cash applied to receipts (sales positive, cash refunds negative;
    /// change is already netted out) + deposits − withdrawals.
    /// </summary>
    public static SessionSummary Summarize(long openingCashMinor, IReadOnlyCollection<Receipt> receipts, IReadOnlyCollection<CashMovement> movements)
    {
        var payments = receipts.SelectMany(r => r.Payments).ToList();
        var lines = receipts.SelectMany(r => r.Lines).ToList();
        var cash = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountMinor);
        var deposits = movements.Where(m => m.Kind == CashMovementKind.Deposit).Sum(m => m.AmountMinor);
        var withdrawals = movements.Where(m => m.Kind == CashMovementKind.Withdrawal).Sum(m => m.AmountMinor);
        var gross = receipts.Sum(r => r.TotalGrossMinor);
        var vat = receipts.Sum(r => r.TotalVatMinor);

        return new SessionSummary(
            receipts.Count,
            receipts.Count(r => r.Kind == ReceiptKind.Sale),
            receipts.Count(r => r.Kind == ReceiptKind.Storno),
            receipts.Count(r => r.Kind == ReceiptKind.Return),
            gross,
            gross - vat,
            vat,
            Enum.GetValues<PaymentMethod>()
                .Select(m => new PaymentTotal(m, payments.Where(p => p.Method == m).Sum(p => p.AmountMinor)))
                .ToList(),
            lines.GroupBy(l => (l.TaxTreatment, l.VatRateBasisPoints))
                .OrderBy(g => g.Key.TaxTreatment)
                .Select(g => new TaxTotal(g.Key.TaxTreatment, g.Key.VatRateBasisPoints, g.Sum(l => l.LineGrossMinor), g.Sum(l => l.NetMinor),
                    g.Sum(l => l.VatMinor)))
                .ToList(),
            openingCashMinor,
            cash,
            deposits,
            withdrawals,
            checked(openingCashMinor + cash + deposits - withdrawals),
            receipts.Count == 0 ? null : receipts.Min(r => r.Number),
            receipts.Count == 0 ? null : receipts.Max(r => r.Number));
    }
}

/// <summary>
/// A cash session of one register (Kassensitzung), from opening with counted cash to the daily closing
/// (Tagesabschluss): counted cash vs. expected cash, totals per payment method and VAT treatment (Z-report).
/// At most one open session per register (partial unique index). Closed sessions are immutable (trigger).
/// </summary>
public sealed class CashSession : IAuditable
{
    public const int MaxNoteLength = 500;

    private CashSession() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public CashSessionStatus Status { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public Guid OpenedBy { get; private set; }
    public long OpeningCashMinor { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public long? ExpectedCashMinor { get; private set; }
    public long? CountedCashMinor { get; private set; }

    /// <summary>Counted − expected: negative = cash missing, positive = surplus.</summary>
    public long? DifferenceMinor { get; private set; }
    public string? ClosingNote { get; private set; }
    public SessionSummary? Summary { get; private set; }

    /// <summary>Opening count by denomination, if the cashier counted that way (its total is <see cref="OpeningCashMinor"/>).</summary>
    public List<DenominationCount>? OpeningDenominations { get; private set; }

    /// <summary>Closing count by denomination, if counted that way (its total is <see cref="CountedCashMinor"/>).</summary>
    public List<DenominationCount>? ClosingDenominations { get; private set; }
    public uint Version { get; private set; }

    public static Result<CashSession> Open(Guid organizationId, Register register, long openingCashMinor, Guid userId, DateTimeOffset now,
        IReadOnlyList<DenominationCount>? denominations = null)
    {
        if (register.OrganizationId != organizationId)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (!register.IsActive)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        if (openingCashMinor < 0)
            return Error.Validation("pos.opening_cash", "The opening cash cannot be negative.");
        if (denominations is not null && CashCount.Total(denominations) != openingCashMinor)
            return Error.Validation("pos.denominations_mismatch", "The denominations do not add up to the opening cash.");
        return new CashSession
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            RegisterId = register.Id,
            Status = CashSessionStatus.Open,
            OpenedAt = now,
            OpenedBy = userId,
            OpeningCashMinor = openingCashMinor,
            OpeningDenominations = denominations?.ToList(),
        };
    }

    public Result Close(SessionSummary summary, long countedCashMinor, string? note, Guid userId, DateTimeOffset now,
        IReadOnlyList<DenominationCount>? denominations = null)
    {
        if (Status != CashSessionStatus.Open)
            return Error.Conflict("pos.session_closed", "The cash session is already closed.");
        if (countedCashMinor < 0)
            return Error.Validation("pos.counted_cash", "The counted cash cannot be negative.");
        if (denominations is not null && CashCount.Total(denominations) != countedCashMinor)
            return Error.Validation("pos.denominations_mismatch", "The denominations do not add up to the counted cash.");
        if (note?.Trim().Length > MaxNoteLength)
            return Error.Validation("pos.note", $"The note is limited to {MaxNoteLength} characters.");
        if (summary.OpeningCashMinor != OpeningCashMinor)
            return Error.Conflict("pos.summary_mismatch", "The summary does not belong to this session.");

        Status = CashSessionStatus.Closed;
        ClosedAt = now;
        ClosedBy = userId;
        Summary = summary;
        ExpectedCashMinor = summary.ExpectedCashMinor;
        CountedCashMinor = countedCashMinor;
        ClosingDenominations = denominations?.ToList();
        DifferenceMinor = countedCashMinor - summary.ExpectedCashMinor;
        ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return Result.Success();
    }
}

/// <summary>Cash put into or taken out of the drawer during a session. Append-only.</summary>
public sealed class CashMovement : IAuditable
{
    public const int MaxReasonLength = 200;

    private CashMovement() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid SessionId { get; private set; }
    public CashMovementKind Kind { get; private set; }

    /// <summary>Always positive; the kind gives the direction.</summary>
    public long AmountMinor { get; private set; }
    public string Reason { get; private set; } = null!;

    /// <summary>Predefined reason (null for movements recorded before codes existed, or free text only).</summary>
    public CashMovementReason? ReasonCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    /// <param name="expectedCashMinor">Cash that should be in the drawer now; a withdrawal cannot exceed it.</param>
    /// <param name="reasonCode">Predefined reason; <paramref name="reason"/> is then optional (the label is stored) except for "Other".</param>
    public static Result<CashMovement> Record(CashSession session, CashMovementKind kind, long amountMinor, string? reason, long expectedCashMinor,
        Guid userId, DateTimeOffset now, CashMovementReason? reasonCode = null)
    {
        if (session.Status != CashSessionStatus.Open)
            return Error.Conflict("pos.session_closed", "The cash session is closed.");
        if (amountMinor <= 0)
            return Error.Validation("pos.amount", "The amount must be positive.");
        if (reason?.Trim().Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"The reason is limited to {MaxReasonLength} characters.");
        var text = CashMovementReasons.Resolve(kind, reasonCode, reason);
        if (!text.IsSuccess)
            return text.Error!;
        if (kind == CashMovementKind.Withdrawal && amountMinor > expectedCashMinor)
            return Error.Conflict("pos.insufficient_cash",
                    $"Only {expectedCashMinor / 100m:0.00} € should be in the drawer; {amountMinor / 100m:0.00} € cannot be withdrawn.")
                .WithDetails(new Dictionary<string, object?> { ["expectedCashMinor"] = expectedCashMinor });
        return new CashMovement
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = session.OrganizationId,
            SessionId = session.Id,
            Kind = kind,
            AmountMinor = amountMinor,
            Reason = text.Value,
            ReasonCode = reasonCode,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }
}
