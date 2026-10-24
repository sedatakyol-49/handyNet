using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Contracts;

/// <summary>Reason of a payout from the till recorded by another module.</summary>
public enum CashPayoutKind
{
    /// <summary>Cash paid to a private seller for a device bought (Ankauf).</summary>
    Ankauf,
}

/// <param name="Reference">Unique source of the payout, e.g. <c>ankauf:{intakeId}</c>; a reference is paid out at most once.</param>
/// <param name="Description">Text stored as the movement reason (e.g. "Ankauf Apple iPhone 13 · IMEI …"); max 200 characters.</param>
public sealed record CashPayoutRequest(Guid RegisterId, CashPayoutKind Kind, long AmountMinor, string Reference, string Description);

public sealed record CashPayoutResult(Guid CashMovementId, Guid SessionId, Guid RegisterId, string RegisterName);

/// <summary>A register as offered for a payout: active, with its open session and expected cash (null without an open session).</summary>
public sealed record PayoutRegister(Guid Id, string Name, Guid? OpenSessionId, long? ExpectedCashMinor);

/// <summary>
/// Payouts from a till for other modules (Ankauf → Pos, ADR-0014). The call joins the caller's open database transaction, so
/// "intake paid + stocked" and "cash taken from the drawer" commit together or not at all. The payout is a cash withdrawal
/// with a system reason code, needs an open cash session, the POS module and the right <c>pos.cash_movement</c>, cannot exceed
/// the expected cash, and is signed by the fiscal layer as <c>SonstigerVorgang</c> like any cash movement.
/// </summary>
public interface ICashPayouts
{
    /// <summary>Whether the current organization uses the POS module (then cash payouts must go through a register).</summary>
    bool IsAvailable { get; }

    /// <summary>Active registers of the current organization with their open session; empty without the POS module.</summary>
    Task<IReadOnlyList<PayoutRegister>> ListRegistersAsync(CancellationToken ct);

    /// <summary>Records the payout (must run inside the caller's transaction; the caller saves and commits).</summary>
    Task<Result<CashPayoutResult>> PayOutAsync(CashPayoutRequest request, CancellationToken ct);
}
