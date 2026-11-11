using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// Gift card codes (ADR-0020): 16 characters from an alphabet without look-alikes (no I, O, 0, 1), about 80 bits, shown as
/// <c>XXXX-XXXX-XXXX-XXXX</c>. The code itself is never stored — only its SHA-256 and the last four characters.
/// </summary>
public static class GiftCardCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 16;

    /// <summary>A new random code (cryptographic RNG), without separators.</summary>
    public static string New()
    {
        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>Upper-cases and removes separators; null when what remains is not a code (wrong length or characters).</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > 64)
            return null;
        var compact = new string(input.Where(c => c is not (' ' or '-')).Select(char.ToUpperInvariant).ToArray());
        return compact.Length == Length && compact.All(Alphabet.Contains) ? compact : null;
    }

    /// <summary>For display and printing on the voucher slip: groups of four.</summary>
    public static string Format(string normalized) => string.Join("-", Enumerable.Range(0, Length / 4).Select(i => normalized.Substring(i * 4, 4)));

    public static string HashOf(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));

    public static string Last4(string normalized) => normalized[^4..];

    /// <summary>What receipts and lists show instead of the code.</summary>
    public static string Masked(string last4) => "••••-" + last4;
}

/// <summary>Stored as text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GiftCardStatus>))]
public enum GiftCardStatus
{
    Active,

    /// <summary>Voided by a manager or by the storno of the issuing receipt: balance 0, never usable again.</summary>
    Voided,
}

/// <summary>Stored as text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GiftCardTransactionType>))]
public enum GiftCardTransactionType
{
    /// <summary>+initial amount: sold at the till or issued as the refund of a return.</summary>
    Issue,

    /// <summary>−amount: used as a means of payment.</summary>
    Redeem,

    /// <summary>+amount: the sale it paid for was cancelled (Storno).</summary>
    Restore,

    /// <summary>−remaining balance: voided.</summary>
    Void,

    /// <summary>0: the card got a new code (the old one is dead), same balance.</summary>
    Reissue,
}

/// <summary>
/// A gift card (Mehrzweck-Gutschein, ADR-0020): sold without VAT, redeemed as a means of payment. The balance changes only
/// together with an append-only <see cref="GiftCardTransaction"/>; it can never be negative or above the initial amount
/// (CHECK), and the database verifies at commit that it equals the sum of the card's transactions. Never deleted.
/// </summary>
public sealed class GiftCard : IAuditable
{
    public const int MaxReasonLength = 200;

    private GiftCard() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>SHA-256 (hex) of the normalized code. The code itself is not stored anywhere.</summary>
    public string CodeHash { get; private set; } = null!;
    public string CodeLast4 { get; private set; } = null!;
    public long InitialMinor { get; private set; }
    public long BalanceMinor { get; private set; }
    public GiftCardStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>The sale receipt that sold the card, or the return receipt that refunded onto it.</summary>
    public Guid IssuedReceiptId { get; private set; }

    /// <summary>Last day the card can be redeemed (Europe/Berlin); null = no expiry.</summary>
    public DateOnly? ExpiresOn { get; private set; }
    public Guid? CustomerId { get; private set; }
    public uint Version { get; private set; }

    /// <summary>31 December of the issue year + <paramref name="validityYears"/> (the regular limitation period ends at a year's end).</summary>
    public static DateOnly ExpiryFor(DateOnly issuedOn, int validityYears) => new(issuedOn.Year + validityYears, 12, 31);

    public static Result<(GiftCard Card, GiftCardTransaction Transaction)> Issue(Guid id, Guid organizationId, string normalizedCode, long amountMinor,
        Guid receiptId, DateOnly? expiresOn, Guid? customerId, Guid userId, DateTimeOffset now)
    {
        if (GiftCardCode.Normalize(normalizedCode) != normalizedCode)
            throw new ArgumentException("A normalized gift card code is required.", nameof(normalizedCode));
        if (amountMinor is <= 0 or > PosPricing.MaxPriceMinor)
            return Error.Validation("pos.gift_card_amount", "The gift card amount must be positive.");
        var card = new GiftCard
        {
            Id = id,
            OrganizationId = organizationId,
            CodeHash = GiftCardCode.HashOf(normalizedCode),
            CodeLast4 = GiftCardCode.Last4(normalizedCode),
            InitialMinor = amountMinor,
            BalanceMinor = amountMinor,
            Status = GiftCardStatus.Active,
            IssuedAt = now,
            IssuedReceiptId = receiptId,
            ExpiresOn = expiresOn,
            CustomerId = customerId,
        };
        return (card, GiftCardTransaction.Record(card, GiftCardTransactionType.Issue, amountMinor, receiptId, null, userId, now));
    }

    public bool IsExpired(DateOnly today) => ExpiresOn is { } last && today > last;

    public bool IsUsable(DateOnly today) => Status == GiftCardStatus.Active && !IsExpired(today);

    /// <summary>Pays <paramref name="amountMinor"/> from the card: active, not expired, and never more than the balance.</summary>
    public Result<GiftCardTransaction> Redeem(long amountMinor, Guid receiptId, DateOnly today, Guid userId, DateTimeOffset now)
    {
        if (amountMinor <= 0)
            return Error.Validation("pos.payment_amount", "Payment amounts must be positive.");
        if (!IsUsable(today))
            return NotUsable();
        if (amountMinor > BalanceMinor)
            return Error.Conflict("pos.gift_card_insufficient",
                    $"The gift card {GiftCardCode.Masked(CodeLast4)} holds {BalanceMinor / 100m:0.00} €; {amountMinor / 100m:0.00} € cannot be paid with it.")
                .WithDetails(new Dictionary<string, object?> { ["balanceMinor"] = BalanceMinor, ["codeLast4"] = CodeLast4 });
        BalanceMinor -= amountMinor;
        return GiftCardTransaction.Record(this, GiftCardTransactionType.Redeem, -amountMinor, receiptId, null, userId, now);
    }

    /// <summary>Gives back what a cancelled sale had paid with the card. A voided card cannot take it back.</summary>
    public Result<GiftCardTransaction> Restore(long amountMinor, Guid receiptId, Guid userId, DateTimeOffset now)
    {
        if (Status != GiftCardStatus.Active)
            return NotUsable();
        if (amountMinor <= 0 || BalanceMinor + amountMinor > InitialMinor)
            return Error.Conflict("pos.gift_card_restore", "The amount cannot be restored to the gift card.");
        BalanceMinor += amountMinor;
        return GiftCardTransaction.Record(this, GiftCardTransactionType.Restore, amountMinor, receiptId, null, userId, now);
    }

    /// <summary>Voids the card: the remaining balance is written off and the card is never usable again.</summary>
    public Result<GiftCardTransaction> Void(string? reason, Guid? receiptId, Guid userId, DateTimeOffset now)
    {
        if (Status == GiftCardStatus.Voided)
            return Error.Conflict("pos.gift_card_not_usable", "The gift card is already voided.");
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"A reason (max {MaxReasonLength} characters) is required.");
        var remaining = BalanceMinor;
        BalanceMinor = 0;
        Status = GiftCardStatus.Voided;
        return GiftCardTransaction.Record(this, GiftCardTransactionType.Void, -remaining, receiptId, text, userId, now);
    }

    /// <summary>
    /// Storno of the receipt that issued the card: only an untouched card can be taken back — once anything was redeemed
    /// the money is partly spent.
    /// </summary>
    public Result<GiftCardTransaction> VoidForStorno(Guid stornoReceiptId, Guid userId, DateTimeOffset now)
    {
        if (Status == GiftCardStatus.Voided)
            return NotUsable();
        if (BalanceMinor != InitialMinor)
            return Error.Conflict("pos.gift_card_already_redeemed",
                    $"The gift card {GiftCardCode.Masked(CodeLast4)} was already (partly) redeemed; the receipt that sold it cannot be cancelled.")
                .WithDetails(new Dictionary<string, object?> { ["codeLast4"] = CodeLast4, ["balanceMinor"] = BalanceMinor });
        return Void("Storno des Verkaufsbelegs", stornoReceiptId, userId, now);
    }

    /// <summary>A new code for the same card and balance (lost or exposed code): the old code stops working.</summary>
    public Result<GiftCardTransaction> Reissue(string newNormalizedCode, string? reason, Guid userId, DateTimeOffset now)
    {
        if (GiftCardCode.Normalize(newNormalizedCode) != newNormalizedCode)
            throw new ArgumentException("A normalized gift card code is required.", nameof(newNormalizedCode));
        if (Status != GiftCardStatus.Active)
            return NotUsable();
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"A reason (max {MaxReasonLength} characters) is required.");
        CodeHash = GiftCardCode.HashOf(newNormalizedCode);
        CodeLast4 = GiftCardCode.Last4(newNormalizedCode);
        return GiftCardTransaction.Record(this, GiftCardTransactionType.Reissue, 0, null, text, userId, now);
    }

    private Error NotUsable() =>
        Error.Conflict("pos.gift_card_not_usable",
                Status == GiftCardStatus.Voided
                    ? $"The gift card {GiftCardCode.Masked(CodeLast4)} was voided."
                    : $"The gift card {GiftCardCode.Masked(CodeLast4)} expired on {ExpiresOn:dd.MM.yyyy}.")
            .WithDetails(new Dictionary<string, object?> { ["codeLast4"] = CodeLast4, ["cardStatus"] = Status.ToString(), ["expiresOn"] = ExpiresOn });
}

/// <summary>One change of a gift card's balance. Append-only (trigger); the card's balance is the sum of its transactions.</summary>
public sealed class GiftCardTransaction : IAuditable
{
    private GiftCardTransaction() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid GiftCardId { get; private set; }
    public GiftCardTransactionType Type { get; private set; }

    /// <summary>Signed: positive adds to the balance, negative takes from it.</summary>
    public long AmountMinor { get; private set; }
    public long BalanceAfterMinor { get; private set; }
    public Guid? ReceiptId { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    internal static GiftCardTransaction Record(GiftCard card, GiftCardTransactionType type, long amountMinor, Guid? receiptId, string? reason, Guid userId,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = card.OrganizationId,
        GiftCardId = card.Id,
        Type = type,
        AmountMinor = amountMinor,
        BalanceAfterMinor = card.BalanceMinor,
        ReceiptId = receiptId,
        Reason = reason,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>Limits for gift cards sold at the till (configuration <c>Pos:GiftCard</c>).</summary>
public sealed record GiftCardRules(long MinMinor, long MaxMinor, int ValidityYears)
{
    public const string LineDescription = "Geschenkgutschein";

    /// <summary>A gift card line: no product, quantity 1, no discount, an amount inside the configured range.</summary>
    public Result CheckLine(int lineNumber, long amountMinor, bool hasProductOrImei, int quantity, long discountMinor, long unitPriceMinor)
    {
        if (hasProductOrImei || quantity != 1 || discountMinor != 0 || (unitPriceMinor != 0 && unitPriceMinor != amountMinor))
            return Error.Validation("pos.gift_card_line",
                $"Line {lineNumber}: a gift card is sold without a product, with quantity 1, without a discount and at its amount.");
        if (amountMinor < MinMinor || amountMinor > MaxMinor)
            return Error.Validation("pos.gift_card_amount",
                    $"Line {lineNumber}: a gift card is sold for {MinMinor / 100m:0.00} € to {MaxMinor / 100m:0.00} €.")
                .WithDetails(new Dictionary<string, object?> { ["lineNumber"] = lineNumber, ["minMinor"] = MinMinor, ["maxMinor"] = MaxMinor });
        return Result.Success();
    }
}
