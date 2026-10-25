using System.Security.Cryptography;
using System.Text;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

public enum CheckInStatus
{
    /// <summary>Waiting for the seller to submit the form on their phone.</summary>
    Open,

    /// <summary>The seller submitted their data; waiting for the employee to verify it against the ID.</summary>
    Submitted,

    /// <summary>Used for an intake (its data now lives there).</summary>
    Consumed,

    /// <summary>Not submitted (or not used) in time.</summary>
    Expired,
}

/// <summary>Unguessable single-use link token: 256 random bits, base64url; only its SHA-256 is stored.</summary>
public static class CheckInToken
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Plausibility check before any database lookup (43 base64url characters).</summary>
    public static bool LooksValid(string? token) => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// QR self check-in (ADR-0015): the shop shows a QR code with a short-lived, single-use link; the seller enters their data
/// (and optionally signs) on their own phone without an account; the employee verifies it against the ID document and
/// takes it into an intake. Submitting is a one-time write through a SECURITY DEFINER function; the personal data is
/// cleared once used for an intake (it then lives in the intake) or, if never used, by the retention job shortly after expiry.
/// </summary>
public sealed class CheckIn : IAuditable
{
    public const int DefaultValidMinutes = 15;
    public const int MaxShopNameLength = 200;

    private CheckIn() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string TokenHash { get; private set; } = null!;

    /// <summary>Shop name shown on the public form (snapshot; the public form has no access to other data).</summary>
    public string ShopName { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    [PersonalData]
    public SellerDetails? Seller { get; private set; }

    [PersonalData]
    public byte[]? SignatureImage { get; private set; }

    public DateTimeOffset? SignedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public Guid? IntakeId { get; private set; }

    /// <summary>When the personal data was removed (consumed or expired unused).</summary>
    public DateTimeOffset? PurgedAt { get; private set; }

    public uint Version { get; private set; }

    public static (CheckIn CheckIn, string Token) Create(Guid organizationId, string shopName, Guid userId, DateTimeOffset now, int validMinutes)
    {
        if (validMinutes is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(validMinutes));
        var token = CheckInToken.New();
        var name = shopName.Trim();
        return (new CheckIn
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            TokenHash = CheckInToken.HashOf(token),
            ShopName = name.Length > MaxShopNameLength ? name[..MaxShopNameLength] : name,
            CreatedAt = now,
            CreatedBy = userId,
            ExpiresAt = now.AddMinutes(validMinutes),
        }, token);
    }

    public CheckInStatus StatusAt(DateTimeOffset now) =>
        ConsumedAt is not null ? CheckInStatus.Consumed
        : SubmittedAt is not null && PurgedAt is null && now < ExpiresAt.AddHours(SubmittedValidHours) ? CheckInStatus.Submitted
        : SubmittedAt is null && now < ExpiresAt ? CheckInStatus.Open
        : CheckInStatus.Expired;

    /// <summary>Submitted data can be taken into an intake for this long after the link expired (the seller is at the counter).</summary>
    public const int SubmittedValidHours = 2;

    /// <summary>Takes the submitted data into an intake once; the check-in keeps no personal data afterwards.</summary>
    public Result<(SellerDetails Seller, byte[]? Signature, DateTimeOffset? SignedAt)> Consume(Guid intakeId, DateTimeOffset now)
    {
        switch (StatusAt(now))
        {
            case CheckInStatus.Consumed:
                return Error.Conflict("ankauf.checkin_used", "This check-in was already used for an intake.");
            case CheckInStatus.Open:
                return Error.Conflict("ankauf.checkin_not_submitted", "The seller has not submitted the check-in yet.");
            case CheckInStatus.Expired:
                return Error.Conflict("ankauf.checkin_expired", "The check-in has expired. Start a new one.");
        }
        var taken = (Seller!, SignatureImage, SignedAt);
        ConsumedAt = now;
        IntakeId = intakeId;
        Purge(now);
        return taken;
    }

    /// <summary>Removes the personal data (used or expired). Idempotent.</summary>
    public void Purge(DateTimeOffset now)
    {
        if (PurgedAt is not null)
            return;
        Seller = null;
        SignatureImage = null;
        PurgedAt = now;
    }
}
