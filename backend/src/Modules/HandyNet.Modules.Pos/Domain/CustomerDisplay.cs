using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// A public, unguessable link that lets a tablet or second device show the customer display of one register (ADR-0023).
/// Only the SHA-256 of the token is stored; the link expires and can be revoked. A register has at most one active link.
/// Rows are never deleted.
/// </summary>
public sealed class DisplayLink : IAuditable
{
    private DisplayLink() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static DisplayLink Create(Guid organizationId, Guid registerId, string tokenHash, Guid userId, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        RegisterId = registerId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
        CreatedBy = userId,
    };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public sealed record DisplayLine(string Description, int Quantity, long UnitPriceMinor, long DiscountMinor, long LineGrossMinor);

public sealed record DisplayPayment(long DueMinor, long TenderedMinor, long ChangeMinor, string? Method);

public sealed record DisplayLoyalty(int Points, long RedeemValueMinor);

/// <summary>
/// What the customer display shows (ADR-0023). Pushed by the till, kept in memory only, shown by the public display page.
/// It must never contain personal data (no customer name, email or phone): the shape has no field for it, and free texts
/// are short.
/// </summary>
public sealed record DisplayState(
    string Phase,
    IReadOnlyList<DisplayLine>? Lines,
    long TotalMinor,
    long DiscountTotalMinor,
    DisplayPayment? Payment = null,
    DisplayLoyalty? Loyalty = null,
    string? Message = null,
    string? ReceiptUrl = null,
    string Currency = Money.Eur,
    string? Locale = null)
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxLines = Receipt.MaxLines;
    public const int MaxDescriptionLength = Receipt.MaxDescriptionLength;
    public const int MaxMessageLength = 500;
    public const int MaxUrlLength = 500;
    public const long MaxAmountMinor = 1_000_000_000_00;

    public static readonly IReadOnlyList<string> Phases = ["idle", "cart", "payment", "done"];

    /// <summary>What a display shows before the till pushed anything (also after a restart of the API).</summary>
    public static readonly DisplayState Idle = new("idle", [], 0, 0);

    /// <summary>Validates the shape and returns the normalized state (trimmed texts, no null list).</summary>
    public Result<DisplayState> Normalize()
    {
        if (Phase is null || !Phases.Contains(Phase))
            return Invalid("phase", "The phase must be idle, cart, payment or done.");
        var lines = Lines ?? [];
        if (lines.Count > MaxLines)
            return Invalid("lines", $"At most {MaxLines} lines.");
        if (lines.Any(l => l is null || string.IsNullOrWhiteSpace(l.Description) || l.Description.Length > MaxDescriptionLength
                           || !Amount(l.UnitPriceMinor) || !Amount(l.DiscountMinor) || !Amount(l.LineGrossMinor) || Math.Abs((long)l.Quantity) > Receipt.MaxQuantity))
            return Invalid("lines", $"Each line needs a description (max {MaxDescriptionLength} characters) and plausible amounts.");
        if (!Amount(TotalMinor) || !Amount(DiscountTotalMinor))
            return Invalid("totalMinor", "The totals are not plausible.");
        if (Payment is { } p && (!Amount(p.DueMinor) || !Amount(p.TenderedMinor) || !Amount(p.ChangeMinor) || p.Method?.Length > 30))
            return Invalid("payment", "The payment figures are not plausible.");
        if (Loyalty is { } l && (l.Points < 0 || !Amount(l.RedeemValueMinor)))
            return Invalid("loyalty", "The loyalty figures are not plausible.");
        if (Message?.Length > MaxMessageLength)
            return Invalid("message", $"The message is limited to {MaxMessageLength} characters.");
        if (!string.IsNullOrWhiteSpace(ReceiptUrl) && !IsLink(ReceiptUrl.Trim()))
            return Invalid("receiptUrl", "The receipt link must be an http(s) URL or a path starting with '/'.");
        if (Currency is not { Length: 3 } || !Currency.All(char.IsAsciiLetterUpper))
            return Invalid("currency", "The currency is a three-letter code.");
        if (Locale is not null && (Locale.Length > 20 || !Locale.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return Invalid("locale", "The locale is a language tag such as de or de-DE.");

        return this with
        {
            Lines = lines.Select(x => x with { Description = x.Description.Trim() }).ToList(),
            Message = string.IsNullOrWhiteSpace(Message) ? null : Message.Trim(),
            ReceiptUrl = string.IsNullOrWhiteSpace(ReceiptUrl) ? null : ReceiptUrl.Trim(),
            Payment = Payment is null ? null : Payment with { Method = string.IsNullOrWhiteSpace(Payment.Method) ? null : Payment.Method.Trim() },
        };
    }

    private static bool Amount(long minor) => minor is >= -MaxAmountMinor and <= MaxAmountMinor;

    private static bool IsLink(string url) =>
        url.Length <= MaxUrlLength
        && ((url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
            || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && string.IsNullOrEmpty(uri.UserInfo)));

    private static Error Invalid(string field, string message) =>
        Error.Validation("pos.display_state_invalid", message).WithDetails(new Dictionary<string, object?> { ["field"] = field });
}
