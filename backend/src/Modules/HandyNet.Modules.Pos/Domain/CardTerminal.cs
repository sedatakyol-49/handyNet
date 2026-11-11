using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// How the till's browser reaches the card terminal (ADR-0021). The server only stores the configuration; it never connects
/// to a terminal or bridge. Stored as text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TerminalConnection>))]
public enum TerminalConnection
{
    /// <summary>No integration: the cashier enters the amount on the terminal and confirms the payment (as before ADR-0021).</summary>
    Manual,

    /// <summary>A dialog in the till plays the terminal (approve / decline / abort / timeout). Test only; refused in Production.</summary>
    Simulated,

    /// <summary>ZVT over TCP through the local SEHA bridge on the till PC (browsers cannot open TCP sockets).</summary>
    ZvtBridge,
}

/// <summary>The editable fields of a terminal profile (create and update).</summary>
public sealed record TerminalProfileSettings(string Name, TerminalConnection Connection, string? Host, int? Port, string? BridgeUrl, string? ZvtPassword);

/// <summary>
/// A card terminal configuration of one register (ADR-0021), modelled on <see cref="PrinterProfile"/>: tenant-owned (RLS),
/// several per register with exactly one default, optimistic concurrency. Configuration only: deleting is allowed (audited).
/// </summary>
public sealed class TerminalProfile : IAuditable
{
    public const int MaxNameLength = 100;
    public const int MaxProfilesPerRegister = 20;
    public const int DefaultZvtPort = 20007;
    public const string DefaultZvtPassword = "000000";

    private TerminalProfile() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public string Name { get; private set; } = null!;
    public TerminalConnection Connection { get; private set; }

    /// <summary>ZvtBridge: IP address or host name of the terminal in the shop's network (reached by the bridge, never by the server).</summary>
    public string? Host { get; private set; }

    /// <summary>ZvtBridge: TCP port of the terminal (default 20007).</summary>
    public int? Port { get; private set; }

    /// <summary>ZvtBridge: URL of the local bridge on the till PC.</summary>
    public string? BridgeUrl { get; private set; }

    /// <summary>ZvtBridge: the terminal's 6-digit ZVT password (registration 06 00). Kept out of the audit log.</summary>
    [PersonalData]
    public string? ZvtPassword { get; private set; }

    public bool IsDefault { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<TerminalProfile> Create(Guid organizationId, Guid registerId, TerminalProfileSettings settings, bool isDefault, DateTimeOffset now)
    {
        var profile = new TerminalProfile
        {
            Id = Guid.CreateVersion7(), OrganizationId = organizationId, RegisterId = registerId, IsDefault = isDefault, CreatedAt = now,
        };
        var applied = profile.Update(settings, now);
        return applied.IsSuccess ? profile : applied.Error!;
    }

    /// <summary>Validates and applies the settings; fields that do not belong to the connection type are cleared.</summary>
    public Result Update(TerminalProfileSettings s, DateTimeOffset now)
    {
        var name = s.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return Error.Validation("pos.terminal_name", $"A terminal name (max {MaxNameLength} characters) is required.");
        if (!Enum.IsDefined(s.Connection))
            return Error.Validation("pos.terminal_connection", "Unknown connection type.");

        string? host = null, bridgeUrl = null, password = null;
        int? port = null;
        if (s.Connection == TerminalConnection.ZvtBridge)
        {
            host = s.Host?.Trim();
            if (string.IsNullOrEmpty(host) || !PrinterProfile.IsValidHost(host))
                return Error.Validation("pos.terminal_host", "Enter the terminal's IP address or host name (without scheme and path).");
            if (s.Port is { } p && p is < 1 or > 65535)
                return Error.Validation("pos.terminal_port", "The port must be between 1 and 65535.");
            port = s.Port ?? DefaultZvtPort;
            bridgeUrl = s.BridgeUrl?.Trim();
            if (string.IsNullOrEmpty(bridgeUrl) || !PrinterProfile.IsValidBridgeUrl(bridgeUrl))
                return Error.Validation("pos.terminal_bridge_url", "The bridge URL must be an http(s) address such as http://localhost:9123.");
            password = string.IsNullOrWhiteSpace(s.ZvtPassword) ? DefaultZvtPassword : s.ZvtPassword.Trim();
            if (password.Length != 6 || !password.All(char.IsAsciiDigit))
                return Error.Validation("pos.terminal_password", "The ZVT password has exactly 6 digits.");
        }

        Name = name;
        Connection = s.Connection;
        Host = host;
        Port = port;
        BridgeUrl = bridgeUrl;
        ZvtPassword = password;
        UpdatedAt = now;
        return Result.Success();
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;

    /// <summary>
    /// The simulated terminal approves payments without any card: test only. Refused in Production unless the staging flag
    /// <c>Pos:Terminal:AllowSimulatedInProduction</c> is set (like the simulated TSE, ADR-0012).
    /// </summary>
    public static Error? CheckSimulatedAllowed(TerminalConnection connection, bool isProduction, bool allowedInProduction) =>
        connection == TerminalConnection.Simulated && isProduction && !allowedInProduction
            ? Error.Conflict("pos.terminal_simulated_not_allowed", "The simulated card terminal must not be used in production.")
            : null;
}

/// <summary>Stored as text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardTransactionKind>))]
public enum CardTransactionKind
{
    /// <summary>Charge of the customer's card (sale).</summary>
    Payment,

    /// <summary>Credit to the customer's card (return).</summary>
    Refund,
}

/// <summary>Stored as text. Pending → Approved | Declined | Aborted | Failed; Approved → Reversed (only while not linked to a receipt).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardTransactionStatus>))]
public enum CardTransactionStatus
{
    Pending,
    Approved,
    Declined,
    Aborted,
    Failed,
    Reversed,
}

/// <summary>What the terminal reported (all optional; lengths are limited, the PAN must be masked).</summary>
public sealed record CardResult(
    string? TerminalId,
    string? TraceNumber,
    string? TerminalReceiptNumber,
    string? CardName,
    string? MaskedPan,
    string? AuthCode,
    string? ResultCode,
    string? ResultText,
    string? CustomerReceiptText,
    string? MerchantReceiptText);

/// <summary>
/// One attempt on a card terminal (ADR-0021), written before the terminal is started so that a card charge without a receipt
/// is never lost: Pending → final result reported by the till → linked to the receipt of the sale (or return) that uses it,
/// or reversed. Final results are immutable (trigger), a transaction is linked to at most one receipt, rows are never deleted.
/// </summary>
public sealed class CardTransaction : IAuditable
{
    public const int MaxShortLength = 20;
    public const int MaxCardNameLength = 60;
    public const int MaxPanLength = 30;
    public const int MaxResultTextLength = 500;
    public const int MaxReceiptTextLength = 4000;

    private CardTransaction() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }

    /// <summary>The profile used (no foreign key: profiles are configuration and may be deleted) and its name at that time.</summary>
    public Guid? TerminalProfileId { get; private set; }
    public string? TerminalName { get; private set; }

    /// <summary>Made with the simulated terminal: no card was charged.</summary>
    public bool IsTest { get; private set; }
    public CardTransactionKind Kind { get; private set; }

    /// <summary>Always positive; the kind gives the direction.</summary>
    public long AmountMinor { get; private set; }
    public CardTransactionStatus Status { get; private set; }
    public string? TerminalId { get; private set; }
    public string? TraceNumber { get; private set; }
    public string? TerminalReceiptNumber { get; private set; }
    public string? CardName { get; private set; }
    public string? MaskedPan { get; private set; }
    public string? AuthCode { get; private set; }
    public string? ResultCode { get; private set; }
    public string? ResultText { get; private set; }
    public string? CustomerReceiptText { get; private set; }
    public string? MerchantReceiptText { get; private set; }

    /// <summary>The receipt whose card payment (or refund) this transaction is. Set once, in the receipt's transaction.</summary>
    public Guid? ReceiptId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? LinkedAt { get; private set; }
    public DateTimeOffset? ReversedAt { get; private set; }
    public string? ReversalText { get; private set; }
    public uint Version { get; private set; }

    public static Result<CardTransaction> Start(Guid organizationId, Guid registerId, CardTransactionKind kind, long amountMinor, TerminalProfile? profile,
        Guid userId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind))
            return Error.Validation("pos.card_transaction_kind", "Unknown kind of card transaction.");
        if (amountMinor is <= 0 or > PosPricing.MaxPriceMinor)
            return Error.Validation("pos.card_transaction_amount", "The amount must be positive.");
        if (profile is not null && (profile.OrganizationId != organizationId || profile.RegisterId != registerId))
            return Error.NotFound("pos.terminal_not_found", "Terminal profile not found.");
        return new CardTransaction
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            RegisterId = registerId,
            TerminalProfileId = profile?.Id,
            TerminalName = profile?.Name,
            IsTest = profile?.Connection == TerminalConnection.Simulated,
            Kind = kind,
            AmountMinor = amountMinor,
            Status = CardTransactionStatus.Pending,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }

    /// <summary>The till reports the terminal's final result. Only once; a repeated identical status is accepted without change.</summary>
    public Result Complete(CardTransactionStatus status, CardResult result, DateTimeOffset now)
    {
        if (status is not (CardTransactionStatus.Approved or CardTransactionStatus.Declined or CardTransactionStatus.Aborted or CardTransactionStatus.Failed))
            return Error.Validation("pos.card_transaction_status", "The result must be Approved, Declined, Aborted or Failed.");
        if (Status == status)
            return Result.Success();
        if (Status != CardTransactionStatus.Pending)
            return Error.Conflict("pos.card_transaction_final", $"The card transaction is already {Status} and cannot be changed.")
                .WithDetails(new Dictionary<string, object?> { ["transactionStatus"] = Status.ToString() });
        var pan = Clean(result.MaskedPan);
        if (pan is not null && !IsMasked(pan))
            return Error.Validation("pos.card_pan_not_masked", "The card number must be masked: at most the first 6 and the last 4 digits.");
        if (TooLong(result.TerminalId, MaxShortLength) || TooLong(result.TraceNumber, MaxShortLength) || TooLong(result.TerminalReceiptNumber, MaxShortLength)
            || TooLong(result.AuthCode, MaxShortLength) || TooLong(result.ResultCode, MaxShortLength) || TooLong(result.CardName, MaxCardNameLength)
            || TooLong(result.ResultText, MaxResultTextLength) || (result.CustomerReceiptText?.Length > MaxReceiptTextLength)
            || (result.MerchantReceiptText?.Length > MaxReceiptTextLength))
            return Error.Validation("pos.card_result_field_length", "A field of the terminal result is too long.");

        Status = status;
        TerminalId = Clean(result.TerminalId);
        TraceNumber = Clean(result.TraceNumber);
        TerminalReceiptNumber = Clean(result.TerminalReceiptNumber);
        CardName = Clean(result.CardName);
        MaskedPan = pan;
        AuthCode = Clean(result.AuthCode);
        ResultCode = Clean(result.ResultCode);
        ResultText = Clean(result.ResultText);
        CustomerReceiptText = string.IsNullOrWhiteSpace(result.CustomerReceiptText) ? null : result.CustomerReceiptText;
        MerchantReceiptText = string.IsNullOrWhiteSpace(result.MerchantReceiptText) ? null : result.MerchantReceiptText;
        CompletedAt = now;
        return Result.Success();
    }

    /// <summary>The till reports that an approved charge was reversed on the terminal. Not possible once a receipt uses it.</summary>
    public Result Reverse(string? text, DateTimeOffset now)
    {
        if (Status == CardTransactionStatus.Reversed)
            return Result.Success();
        if (Status != CardTransactionStatus.Approved)
            return Error.Conflict("pos.card_transaction_not_approved", "Only an approved card transaction can be reversed.");
        if (ReceiptId is not null)
            return Error.Conflict("pos.card_transaction_linked", "The card transaction belongs to a receipt. Cancel or return the receipt instead.");
        if (TooLong(text, MaxResultTextLength))
            return Error.Validation("pos.card_result_field_length", "The reversal text is too long.");
        Status = CardTransactionStatus.Reversed;
        ReversedAt = now;
        ReversalText = Clean(text);
        return Result.Success();
    }

    /// <summary>
    /// Whether this transaction can settle a card payment (or refund) of <paramref name="amountMinor"/> on a receipt of
    /// <paramref name="registerId"/>: same register, right kind, approved, exactly the amount, not used by another receipt.
    /// </summary>
    public Result CheckUsable(Guid registerId, CardTransactionKind kind, long amountMinor)
    {
        string? reason =
            RegisterId != registerId ? "register"
            : Kind != kind ? "kind"
            : Status != CardTransactionStatus.Approved ? "status"
            : ReceiptId is not null ? "linked"
            : AmountMinor != amountMinor ? "amount"
            : null;
        return reason is null
            ? Result.Success()
            : Invalid(reason);
    }

    public static Error Invalid(string reason) =>
        Error.Conflict("pos.card_transaction_invalid",
                "The card transaction cannot be used for this payment (it must be an approved transaction of this register over exactly the card amount, not used by another receipt).")
            .WithDetails(new Dictionary<string, object?> { ["reason"] = reason });

    /// <summary>Links the transaction to the receipt that uses it (same database transaction as the receipt).</summary>
    public Result LinkTo(Guid receiptId, DateTimeOffset now)
    {
        if (Status != CardTransactionStatus.Approved || ReceiptId is not null)
            return Invalid(ReceiptId is not null ? "linked" : "status");
        ReceiptId = receiptId;
        LinkedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// A masked card number shows at most the first 6 and the last 4 digits: digits only at the start (≤ 6) and at the end
    /// (≤ 4), mask characters in between. A full PAN (or anything with more digits) is refused — it is never stored.
    /// </summary>
    public static bool IsMasked(string pan)
    {
        if (pan.Length is 0 or > MaxPanLength || pan.Any(c => !char.IsAsciiDigit(c) && !IsMaskCharacter(c) && !IsSeparator(c)))
            return false;
        // Group separators carry no information: "4111 11** **** 1111" is judged as "411111******1111".
        var compact = new string(pan.Where(c => !IsSeparator(c)).ToArray());
        if (compact.Length == 0)
            return false;
        var leading = compact.TakeWhile(char.IsAsciiDigit).Count();
        if (leading == compact.Length)
            return compact.Length <= 4; // only the last digits, nothing else
        var trailing = compact.Reverse().TakeWhile(char.IsAsciiDigit).Count();
        var middle = compact[leading..^trailing];
        return leading <= 6 && trailing <= 4 && !middle.Any(char.IsAsciiDigit);
    }

    private static bool IsMaskCharacter(char c) => c is '*' or 'x' or 'X' or '#' or '•' or '.';

    private static bool IsSeparator(char c) => c is ' ' or '-';

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TooLong(string? value, int max) => value?.Trim().Length > max;
}
