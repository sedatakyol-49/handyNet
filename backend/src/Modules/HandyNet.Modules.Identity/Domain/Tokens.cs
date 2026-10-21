using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Identity.Domain;

/// <summary>
/// Opaque bearer secrets: 256 random bits, base64url. Only the SHA-256 hash is ever stored, so a database leak
/// does not yield usable tokens. (A fast hash is fine here: the input has full entropy, unlike a password.)
/// </summary>
public static class OpaqueToken
{
    public const int MaxLength = 128;

    public static string NewValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class TokenErrors
{
    public static readonly Error RefreshInvalid = Error.Unauthorized("auth.refresh_token_invalid", "The refresh token is not valid. Please sign in again.");
    public static readonly Error RefreshExpired = Error.Unauthorized("auth.refresh_token_expired", "The session has expired. Please sign in again.");
    public static readonly Error RefreshReused = Error.Unauthorized("auth.refresh_token_reused",
        "This refresh token was already used. All sessions of this sign-in were ended for safety. Please sign in again.");

    public static readonly Error LinkInvalid = Error.Validation("auth.token_invalid", "The link is invalid or has already been used.");
    public static readonly Error LinkExpired = Error.Validation("auth.token_expired", "The link has expired. Please request a new one.");
}

public enum RefreshTokenRevocation
{
    Logout,
    LogoutAll,
    Reuse,
    PasswordChanged,
    PasswordReset,

    /// <summary>A platform admin disabled the account.</summary>
    AccountDisabled,

    /// <summary>A platform admin ended all sessions of the user.</summary>
    AdminRevoked,
}

/// <summary>
/// One refresh token of a sign-in session ("family"). Every use rotates it: the token is marked used and a successor
/// in the same family is issued. Presenting a used token again means it was copied — the whole family is revoked
/// (reuse detection), so both the thief and the victim must sign in again.
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public Guid? ReplacedById { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public RefreshTokenRevocation? RevocationReason { get; private set; }
    public uint Version { get; private set; }

    public bool IsActive(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;

    /// <summary>Starts a new session (login) and returns the token together with its only plaintext copy.</summary>
    public static (RefreshToken Token, string Value) StartFamily(Guid userId, DateTimeOffset now, TimeSpan lifetime) =>
        Issue(userId, Guid.CreateVersion7(), now, lifetime);

    private static (RefreshToken, string) Issue(Guid userId, Guid familyId, DateTimeOffset now, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Refresh token lifetime must be positive.");
        var value = OpaqueToken.NewValue();
        return (new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = OpaqueToken.Hash(value),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        }, value);
    }

    /// <summary>
    /// Uses this token: on success it is consumed and its successor (same family, fresh lifetime) is returned.
    /// <see cref="TokenErrors.RefreshReused"/> tells the caller to revoke the family.
    /// </summary>
    public Result<(RefreshToken Token, string Value)> Rotate(DateTimeOffset now, TimeSpan lifetime)
    {
        if (RevokedAt is not null)
            return TokenErrors.RefreshInvalid;
        if (UsedAt is not null)
            return TokenErrors.RefreshReused;
        if (ExpiresAt <= now)
            return TokenErrors.RefreshExpired;

        var (next, value) = Issue(UserId, FamilyId, now, lifetime);
        UsedAt = now;
        ReplacedById = next.Id;
        return (next, value);
    }

    public void Revoke(DateTimeOffset now, RefreshTokenRevocation reason)
    {
        if (RevokedAt is not null)
            return;
        RevokedAt = now;
        RevocationReason = reason;
    }
}

public enum AccountTokenPurpose
{
    EmailVerification,
    PasswordReset,
}

/// <summary>Single-use token sent by email (verification or password reset). Stored hashed; short-lived.</summary>
public sealed class AccountToken
{
    public static readonly TimeSpan DefaultPasswordResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultEmailVerificationLifetime = TimeSpan.FromHours(48);

    private AccountToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public AccountTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public uint Version { get; private set; }

    public static (AccountToken Token, string Value) Issue(Guid userId, AccountTokenPurpose purpose, DateTimeOffset now, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Token lifetime must be positive.");
        var value = OpaqueToken.NewValue();
        return (new AccountToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Purpose = purpose,
            TokenHash = OpaqueToken.Hash(value),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        }, value);
    }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;

    /// <summary>Consumes the token. Used or superseded tokens are invalid; expired ones get their own error.</summary>
    public Result Use(DateTimeOffset now)
    {
        if (UsedAt is not null || RevokedAt is not null)
            return TokenErrors.LinkInvalid;
        if (ExpiresAt <= now)
            return TokenErrors.LinkExpired;
        UsedAt = now;
        return Result.Success();
    }

    /// <summary>Superseded by a newer token of the same purpose, or no longer needed.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (UsedAt is null && RevokedAt is null)
            RevokedAt = now;
    }
}
