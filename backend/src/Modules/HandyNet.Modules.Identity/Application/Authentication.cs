using System.Security.Claims;
using System.Text;
using FluentValidation;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HandyNet.Modules.Identity.Application;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "handynet";
    public string Audience { get; set; } = "handynet";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supplied via secrets / environment, never committed.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Access token lifetime. Kept at 60 in appsettings until the web app refreshes tokens; 15 is the target
    /// (a revoked session keeps working until its access token expires).
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Lifetime of a refresh token; every rotation starts a fresh lifetime (sliding session).</summary>
    public int RefreshTokenDays { get; set; } = 14;

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Configure it via user-secrets or environment.");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    Guid UserId,
    string Email,
    string DisplayName,
    bool IsPlatformAdmin,
    bool EmailVerified);

internal sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(OpaqueToken.MaxLength);
}

internal sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(OpaqueToken.MaxLength);
}

internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(200);
    }
}

internal sealed class AuthenticationService(
    HandyNetDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<JwtOptions> jwtOptions,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<AuthenticationService> logger)
{
    private static readonly Error InvalidCredentials = Error.Validation("auth.invalid_credentials", "Email or password is incorrect.");
    private static readonly Error LockedOut = Error.Forbidden("auth.locked_out", "Too many failed attempts. Try again later.");

    private TimeSpan RefreshLifetime => TimeSpan.FromDays(jwtOptions.Value.RefreshTokenDays);

    public async Task<Result<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == email, ct);
        var now = clock.UtcNow;

        if (user is null || !user.IsActive)
        {
            // Hash anyway so response timing does not reveal whether the account exists.
            hasher.HashPassword(User.Create(email, "x", now), request.Password);
            return InvalidCredentials;
        }

        if (user.IsLockedOut(now))
            return LockedOut;

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.RecordFailedLogin(now);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Failed login for user {UserId}", user.Id);
            return InvalidCredentials;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.SetPasswordHash(hasher.HashPassword(user, request.Password));

        user.RecordSuccessfulLogin(now);
        var response = StartSession(user, now);
        await db.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>Rotates the refresh token and issues a new access token (user data re-read, e.g. admin flag).</summary>
    public async Task<Result<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var hash = OpaqueToken.Hash(request.RefreshToken);
        var token = await db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
            return TokenErrors.RefreshInvalid;

        var rotated = token.Rotate(now, RefreshLifetime);
        if (!rotated.IsSuccess)
        {
            if (rotated.Error == TokenErrors.RefreshReused)
            {
                db.ChangeTracker.Clear();
                var revoked = await RevokeFamilyAsync(token.FamilyId, RefreshTokenRevocation.Reuse, now, ct);
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoked {Count} token(s) of family {FamilyId}",
                    token.UserId, revoked, token.FamilyId);
            }
            return rotated.Error!;
        }

        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == token.UserId, ct);
        if (user is null || !user.IsActive)
            return TokenErrors.RefreshInvalid;

        var (next, value) = rotated.Value;
        db.Add(next);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A parallel request rotated the same token first; this copy lost the race (no theft implied).
            return TokenErrors.RefreshInvalid;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return TokenErrors.RefreshInvalid;
        }

        return CreateResponse(user, now, value, next.ExpiresAt);
    }

    /// <summary>Ends the session the refresh token belongs to. Unknown tokens are ignored (idempotent).</summary>
    public async Task LogoutAsync(LogoutRequest request, CancellationToken ct)
    {
        var hash = OpaqueToken.Hash(request.RefreshToken);
        var familyId = await db.Set<RefreshToken>().Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (familyId is { } family)
            await RevokeFamilyAsync(family, RefreshTokenRevocation.Logout, clock.UtcNow, ct);
    }

    /// <summary>Ends every session of the current user (all devices).</summary>
    public async Task LogoutEverywhereAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        await SessionRevocation.StageRevokeAllAsync(db, currentUser.UserId, RefreshTokenRevocation.LogoutAll, now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Changes the password, ends all other sessions and starts a new one for this client.</summary>
    public async Task<Result<TokenResponse>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == currentUser.UserId && u.IsActive, ct);
        if (user is null)
            return TokenErrors.RefreshInvalid;
        if (user.IsLockedOut(now))
            return LockedOut;

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            user.RecordFailedLogin(now);
            await db.SaveChangesAsync(ct);
            return Error.Validation("auth.current_password_invalid", "The current password is incorrect.");
        }

        if (PasswordPolicy.Check(request.NewPassword) is { } weak)
            return weak;

        user.SetPasswordHash(hasher.HashPassword(user, request.NewPassword));
        await SessionRevocation.StageRevokeAllAsync(db, user.Id, RefreshTokenRevocation.PasswordChanged, now, ct);
        var response = StartSession(user, now);
        await db.SaveChangesAsync(ct); // password + revocations + new session: one transaction
        return response;
    }

    private async Task<int> RevokeFamilyAsync(Guid familyId, RefreshTokenRevocation reason, DateTimeOffset now, CancellationToken ct)
    {
        var family = await db.Set<RefreshToken>().Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in family)
            t.Revoke(now, reason);
        await db.SaveChangesAsync(ct);
        return family.Count;
    }

    private TokenResponse StartSession(User user, DateTimeOffset now)
    {
        var (refresh, value) = RefreshToken.StartFamily(user.Id, now, RefreshLifetime);
        db.Add(refresh);
        return CreateResponse(user, now, value, refresh.ExpiresAt);
    }

    private TokenResponse CreateResponse(User user, DateTimeOffset now, string refreshToken, DateTimeOffset refreshExpiresAt)
    {
        var options = jwtOptions.Value;
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (user.IsPlatformAdmin)
            claims.Add(new Claim(HandyNetClaims.PlatformAdmin, "true"));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.CreateKey(), SecurityAlgorithms.HmacSha256),
        });

        return new TokenResponse(token, expires, refreshToken, refreshExpiresAt, user.Id, user.Email, user.DisplayName,
            user.IsPlatformAdmin, user.EmailVerified);
    }
}

internal static class SessionRevocation
{
    /// <summary>Stages revocation of all live refresh tokens of a user; the caller commits with its other changes.</summary>
    public static async Task StageRevokeAllAsync(HandyNetDbContext db, Guid userId, RefreshTokenRevocation reason, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var t in live)
            t.Revoke(now, reason);
    }
}
