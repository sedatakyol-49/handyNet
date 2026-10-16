using System.Security.Claims;
using FluentValidation;
using HandyNet.Modules.Identity.Application;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace HandyNet.Modules.Identity;

public static class IdentityModule
{
    /// <summary>Login, registration, link confirmation: per client IP and minute (<c>RateLimits:AuthPerMinute</c>).</summary>
    public const string AuthRateLimitPolicy = "auth";

    /// <summary>Token refresh and logout: per client IP and minute (<c>RateLimits:RefreshPerMinute</c>).</summary>
    public const string RefreshRateLimitPolicy = "auth-refresh";

    /// <summary>Endpoints that send an email (reset request, verification resend): per client IP and hour (<c>RateLimits:AccountEmailsPerHour</c>).</summary>
    public const string AccountEmailRateLimitPolicy = "account-email";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section))
            .Validate(o => o.AccessTokenMinutes > 0 && o.RefreshTokenDays > 0, "Jwt:AccessTokenMinutes and Jwt:RefreshTokenDays must be positive.")
            .ValidateOnStart();
        services.AddOptions<AccountTokenOptions>().Bind(configuration.GetSection(AccountTokenOptions.Section))
            .Validate(o => o.PasswordResetTokenMinutes > 0 && o.EmailVerificationTokenHours > 0,
                "Identity:PasswordResetTokenMinutes and Identity:EmailVerificationTokenHours must be positive.")
            .ValidateOnStart();
        services.AddSingleton<IModuleModel, Infrastructure.IdentityModel>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<AccountRecoveryService>();
        services.AddValidatorsFromAssemblyContaining<AuthenticationService>(includeInternalTypes: true);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/login", async (LoginRequest request, AuthenticationService service, CancellationToken ct) =>
                (await service.LoginAsync(request, ct)).ToHttp(Results.Ok))
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy);

        // Anonymous: the access token may already have expired; the refresh token is the credential.
        auth.MapPost("/refresh", async (RefreshRequest request, IValidator<RefreshRequest> validator,
                AuthenticationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.RefreshAsync(request, ct)).ToHttp(Results.Ok))
            .AllowAnonymous()
            .RequireRateLimiting(RefreshRateLimitPolicy);

        auth.MapPost("/logout", async (LogoutRequest request, IValidator<LogoutRequest> validator,
                AuthenticationService service, CancellationToken ct) =>
            {
                if (await validator.ProblemIfInvalidAsync(request, ct) is { } invalid)
                    return invalid;
                await service.LogoutAsync(request, ct);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(RefreshRateLimitPolicy);

        auth.MapPost("/logout-all", async (AuthenticationService service, CancellationToken ct) =>
            {
                await service.LogoutEverywhereAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization();

        auth.MapPost("/change-password", async (ChangePasswordRequest request, IValidator<ChangePasswordRequest> validator,
                AuthenticationService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ChangePasswordAsync(request, ct)).ToHttp(Results.Ok))
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimitPolicy);

        // Same 202 for every syntactically valid request, whether or not the account exists.
        auth.MapPost("/password-reset/request", async (PasswordResetRequest request, IValidator<PasswordResetRequest> validator,
                AccountRecoveryService service, CancellationToken ct) =>
            {
                if (await validator.ProblemIfInvalidAsync(request, ct) is { } invalid)
                    return invalid;
                await service.RequestPasswordResetAsync(request, ct);
                return Results.Accepted();
            })
            .AllowAnonymous()
            .RequireRateLimiting(AccountEmailRateLimitPolicy);

        auth.MapPost("/password-reset/confirm", async (PasswordResetConfirmRequest request, IValidator<PasswordResetConfirmRequest> validator,
                AccountRecoveryService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ResetPasswordAsync(request, ct)).ToHttp(Results.NoContent))
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy);

        // Anonymous: the link may be opened on a device where the user is not signed in.
        auth.MapPost("/verify-email", async (VerifyEmailRequest request, IValidator<VerifyEmailRequest> validator,
                AccountRecoveryService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.VerifyEmailAsync(request, ct)).ToHttp(Results.NoContent))
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy);

        auth.MapPost("/verify-email/resend", async (ResendVerificationRequest request, IValidator<ResendVerificationRequest> validator,
                AccountRecoveryService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.ResendVerificationAsync(request, ct)).ToHttp(() => Results.Accepted()))
            .RequireAuthorization()
            .RequireRateLimiting(AccountEmailRateLimitPolicy);

        return app;
    }

    /// <summary>
    /// Creates or promotes the configured platform administrator. Idempotent; runs at startup. The configured
    /// address is trusted as verified (it comes from the operator, not from a self-registration).
    /// </summary>
    public static async Task EnsurePlatformAdminAsync(IServiceProvider services, string email, string password, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentityModule));
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var normalized = User.NormalizeEmail(email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == normalized, ct);

        if (user is null)
        {
            if (PasswordPolicy.Check(password) is { } weak)
                throw new InvalidOperationException($"Bootstrap admin password rejected: {weak.Message}");

            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            user = User.Create(normalized, "Platform Admin", now);
            user.SetPasswordHash(hasher.HashPassword(user, password));
            db.Add(user);
        }

        if (!user.IsPlatformAdmin)
        {
            user.GrantPlatformAdmin();
            logger.LogInformation("Granted platform admin to {UserId}", user.Id);
        }

        user.MarkEmailVerified(now);
        await db.SaveChangesAsync(ct);
    }
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.TryParse(Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
        ? id
        : throw new InvalidOperationException("No authenticated user.");

    public bool IsPlatformAdmin => Principal?.HasClaim(HandyNetClaims.PlatformAdmin, "true") == true;
}
