using System.Security.Claims;
using System.Text;
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

    public int AccessTokenMinutes { get; set; } = 60;

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Configure it via user-secrets or environment.");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid UserId, string Email, string DisplayName, bool IsPlatformAdmin);

internal sealed class AuthenticationService(
    HandyNetDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<JwtOptions> jwtOptions,
    IClock clock,
    ILogger<AuthenticationService> logger)
{
    private static readonly Error InvalidCredentials = Error.Validation("auth.invalid_credentials", "Email or password is incorrect.");

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
            return Error.Forbidden("auth.locked_out", "Too many failed attempts. Try again later.");

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
        await db.SaveChangesAsync(ct);
        return Issue(user, now);
    }

    private TokenResponse Issue(User user, DateTimeOffset now)
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

        return new TokenResponse(token, expires, user.Id, user.Email, user.DisplayName, user.IsPlatformAdmin);
    }
}
