using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>How many coins/notes of one euro denomination were counted.</summary>
public sealed record DenominationCount(long ValueMinor, int Count)
{
    public long TotalMinor => checked(ValueMinor * Count);
}

/// <summary>A cash count by denomination (opening or closing of a session). Stored with the session as counted.</summary>
public static class CashCount
{
    public const int MaxCountPerDenomination = 100_000;

    /// <summary>Euro coins and notes in cents.</summary>
    public static readonly IReadOnlyList<long> EuroDenominations = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 50_000];

    /// <summary>
    /// Validates a count (known euro denominations, each at most once, counts 0..100,000) and returns it sorted from the
    /// largest denomination, without zero entries. Total = Σ value × count.
    /// </summary>
    public static Result<IReadOnlyList<DenominationCount>> Normalize(IReadOnlyCollection<DenominationCount> counts)
    {
        if (counts.FirstOrDefault(c => !EuroDenominations.Contains(c.ValueMinor)) is { } unknown)
            return Error.Validation("pos.denomination_unknown", $"{unknown.ValueMinor} cents is not a euro coin or note.");
        if (counts.Select(c => c.ValueMinor).Distinct().Count() != counts.Count)
            return Error.Validation("pos.denomination_duplicate", "Each denomination may appear only once.");
        if (counts.Any(c => c.Count is < 0 or > MaxCountPerDenomination))
            return Error.Validation("pos.denomination_count", $"Counts must be between 0 and {MaxCountPerDenomination}.");
        return counts.Where(c => c.Count > 0).OrderByDescending(c => c.ValueMinor).ToList();
    }

    public static long Total(IEnumerable<DenominationCount> counts) => checked(counts.Sum(c => c.TotalMinor));

    /// <summary>
    /// The amount to store: from the denominations if given (and then it must equal a separately entered amount), otherwise
    /// the entered amount. One of both is required.
    /// </summary>
    public static Result<(long AmountMinor, IReadOnlyList<DenominationCount>? Counts)> Resolve(long? amountMinor,
        IReadOnlyCollection<DenominationCount>? counts, string what)
    {
        if (counts is null)
            return amountMinor is { } a ? (a, (IReadOnlyList<DenominationCount>?)null) : Error.Validation("pos.cash_amount_required",
                $"Enter the {what} as an amount or as denomination counts.");
        var normalized = Normalize(counts);
        if (!normalized.IsSuccess)
            return normalized.Error!;
        var total = Total(normalized.Value);
        if (amountMinor is { } entered && entered != total)
            return Error.Validation("pos.denominations_mismatch",
                    $"The denominations add up to {total / 100m:0.00} €, not {entered / 100m:0.00} €.")
                .WithDetails(new Dictionary<string, object?> { ["denominationTotalMinor"] = total, ["amountMinor"] = entered });
        return (total, normalized.Value);
    }
}

/// <summary>Predefined reasons for cash in/out (stored as text). <see cref="Other"/> needs a free-text reason.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CashMovementReason>))]
public enum CashMovementReason
{
    /// <summary>Wechselgeld (deposit).</summary>
    ChangeFund,

    /// <summary>Bankeinzahlung / Abschöpfung (withdrawal).</summary>
    BankDeposit,

    /// <summary>Ausgabe, e.g. small purchases paid from the drawer (withdrawal).</summary>
    Expense,

    /// <summary>Privatentnahme (withdrawal).</summary>
    OwnerWithdrawal,

    /// <summary>Privateinlage (deposit).</summary>
    OwnerDeposit,

    Other,

    /// <summary>
    /// Ankauf: cash paid to a private seller for a bought device (withdrawal). Reserved for the Ankauf payout
    /// (<c>ICashPayouts</c>); never offered or accepted for manual cash movements.
    /// </summary>
    Ankauf,
}

public static class CashMovementReasons
{
    public sealed record Definition(CashMovementReason Code, CashMovementKind Kind, string Label);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(CashMovementReason.ChangeFund, CashMovementKind.Deposit, "Wechselgeld"),
        new(CashMovementReason.OwnerDeposit, CashMovementKind.Deposit, "Privateinlage"),
        new(CashMovementReason.Other, CashMovementKind.Deposit, "Sonstige Einlage"),
        new(CashMovementReason.BankDeposit, CashMovementKind.Withdrawal, "Bankeinzahlung"),
        new(CashMovementReason.Expense, CashMovementKind.Withdrawal, "Ausgabe"),
        new(CashMovementReason.OwnerWithdrawal, CashMovementKind.Withdrawal, "Privatentnahme"),
        new(CashMovementReason.Other, CashMovementKind.Withdrawal, "Sonstige Entnahme"),
    ];

    /// <summary>System reasons: recorded by other modules through a contract, never selectable for manual movements.</summary>
    public static readonly IReadOnlyList<Definition> System =
    [
        new(CashMovementReason.Ankauf, CashMovementKind.Withdrawal, "Ankauf"),
    ];

    public static bool IsSystem(CashMovementReason? code) => code is { } c && System.Any(d => d.Code == c);

    /// <summary>
    /// The stored reason text: the free text if given, otherwise the predefined label. A code must fit the kind; "Other"
    /// and movements without a code need a free text.
    /// </summary>
    public static Result<string> Resolve(CashMovementKind kind, CashMovementReason? code, string? text)
    {
        var free = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (code is null)
            return free is null ? Error.Validation("pos.reason", "A reason is required.") : free;
        if (IsSystem(code))
            return Error.Validation("pos.reason_code_reserved", $"The reason '{code}' is recorded automatically and cannot be chosen.");
        var definition = All.FirstOrDefault(d => d.Code == code && d.Kind == kind);
        if (definition is null)
            return Error.Validation("pos.reason_code", $"The reason '{code}' does not fit a {kind.ToString().ToLowerInvariant()}.");
        if (code == CashMovementReason.Other && free is null)
            return Error.Validation("pos.reason", "Describe the reason for \"Other\".");
        return free ?? definition.Label;
    }
}
