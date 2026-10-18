using System.Security.Cryptography;
using System.Text;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>Rules for a POS PIN: 4–6 ASCII digits, not one repeated digit and not a plain ascending/descending run.</summary>
public static class PinPolicy
{
    public const int MinLength = 4;
    public const int MaxLength = 6;

    public static Error? Check(string? pin)
    {
        if (pin is null || pin.Length is < MinLength or > MaxLength || !pin.All(char.IsAsciiDigit))
            return Error.Validation("pos.pin_format", $"The PIN must have {MinLength} to {MaxLength} digits.");
        if (pin.All(c => c == pin[0]) || IsRun(pin, +1) || IsRun(pin, -1))
            return Error.Validation("pos.pin_too_simple", "This PIN is too easy to guess (repeated digits or a sequence like 1234).");
        return null;
    }

    private static bool IsRun(string pin, int step)
    {
        for (var i = 1; i < pin.Length; i++)
            if (pin[i] - pin[i - 1] != step)
                return false;
        return true;
    }
}

/// <summary>
/// Salted PBKDF2-SHA256 hash of a PIN (<c>pbkdf2-sha256$iterations$salt$hash</c>, base64). A 4–6 digit PIN has few
/// combinations, so the real protection is the attempt limit and lockout (<see cref="CashierPin"/>) plus rate limiting;
/// the hash keeps the PIN itself out of the database and backups.
/// </summary>
public static class PinHasher
{
    private const string Scheme = "pbkdf2-sha256";
    public const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Opaque random tokens (cashier sessions, approvals, public receipt links); only their SHA-256 is stored.</summary>
public static class OpaqueToken
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Plausibility check before any database lookup (43 base64url characters).</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Outcome of a PIN check.</summary>
public enum PinCheck
{
    Valid,
    Invalid,
    Locked,
    NotSet,
}

/// <summary>
/// The POS PIN of one member of an organization (shared till: switch cashier, approve as manager). Hashed; after
/// <see cref="MaxFailedAttempts"/> wrong entries in a row the PIN is locked for <see cref="LockoutMinutes"/> minutes.
/// Setting a new PIN lifts the lockout. Rows are never deleted; clearing sets the hash to null.
/// </summary>
public sealed class CashierPin : IAuditable
{
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;

    private CashierPin() { }

    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Never copied into the audit log.</summary>
    [PersonalData]
    public string? PinHash { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public uint Version { get; private set; }

    public bool HasPin => PinHash is not null;

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public static CashierPin For(Guid organizationId, Guid userId, DateTimeOffset now, Guid by) => new()
    {
        OrganizationId = organizationId,
        UserId = userId,
        UpdatedAt = now,
        UpdatedBy = by,
    };

    /// <summary>Sets (or with null clears) the PIN; resets failed attempts and lockout.</summary>
    public Result Set(string? pin, Guid by, DateTimeOffset now)
    {
        if (pin is not null && PinPolicy.Check(pin) is { } invalid)
            return invalid;
        PinHash = pin is null ? null : PinHasher.Hash(pin);
        FailedAttempts = 0;
        LockedUntil = null;
        UpdatedAt = now;
        UpdatedBy = by;
        return Result.Success();
    }

    /// <summary>Checks a PIN and records the attempt (a lockout ends by time; a correct PIN resets the counter).</summary>
    public PinCheck Verify(string pin, DateTimeOffset now)
    {
        if (!HasPin)
            return PinCheck.NotSet;
        if (IsLocked(now))
            return PinCheck.Locked;
        if (LockedUntil is not null)
        {
            LockedUntil = null; // lockout over: a fresh series of attempts
            FailedAttempts = 0;
        }
        if (PinHasher.Verify(pin, PinHash))
        {
            FailedAttempts = 0;
            return PinCheck.Valid;
        }
        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts)
        {
            LockedUntil = now.AddMinutes(LockoutMinutes);
            return PinCheck.Locked;
        }
        return PinCheck.Invalid;
    }

    public int RemainingAttempts => Math.Max(0, MaxFailedAttempts - FailedAttempts);
}

/// <summary>Why a cashier session ended. Stored as text.</summary>
public enum CashierSessionEnd
{
    /// <summary>The till was locked (button or inactivity).</summary>
    Locked,

    /// <summary>Another cashier signed in on the same register and device login.</summary>
    Replaced,
}

/// <summary>
/// A cashier signed in with their PIN on a shared device: the till acts as <see cref="CashierUserId"/> (receipts, rights)
/// while the device stays logged in as <see cref="DeviceUserId"/>. Bound to one organization and register; valid until
/// <see cref="ExpiresAt"/>, ended by locking, and (with the register's auto-lock) after inactivity. The token is stored
/// only as a hash.
/// </summary>
public sealed class CashierSession : IAuditable
{
    private CashierSession() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public Guid CashierUserId { get; private set; }
    public Guid DeviceUserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public CashierSessionEnd? EndedReason { get; private set; }

    /// <summary>Last-seen updates are written at most this often (keeps the per-request cost low).</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

    public static CashierSession Start(Guid organizationId, Guid registerId, Guid cashierUserId, Guid deviceUserId, string tokenHash, DateTimeOffset now,
        TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        RegisterId = registerId,
        CashierUserId = cashierUserId,
        DeviceUserId = deviceUserId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
        LastSeenAt = now,
    };

    /// <summary>Active: not ended, not expired, and (with auto-lock) used within the last <paramref name="autoLockMinutes"/>.</summary>
    public bool IsActive(DateTimeOffset now, int? autoLockMinutes) =>
        EndedAt is null && ExpiresAt > now && (autoLockMinutes is not > 0 || LastSeenAt.AddMinutes(autoLockMinutes.Value) > now);

    public bool NeedsTouch(DateTimeOffset now) => now - LastSeenAt >= TouchInterval;

    public void Touch(DateTimeOffset now) => LastSeenAt = now;

    public void End(CashierSessionEnd reason, DateTimeOffset now)
    {
        if (EndedAt is not null)
            return;
        EndedAt = now;
        EndedReason = reason;
    }
}
