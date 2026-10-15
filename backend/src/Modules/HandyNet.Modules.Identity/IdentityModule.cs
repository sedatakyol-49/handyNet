using System.Security.Claims;
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
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section));
        services.AddSingleton<IModuleModel, Infrastructure.IdentityModel>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<AuthenticationService>();
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

        return app;
    }

    /// <summary>Creates or promotes the configured platform administrator. Idempotent; runs at startup.</summary>
    public static async Task EnsurePlatformAdminAsync(IServiceProvider services, string email, string password, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentityModule));
        var normalized = User.NormalizeEmail(email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == normalized, ct);

        if (user is null)
        {
            if (PasswordPolicy.Check(password) is { } weak)
                throw new InvalidOperationException($"Bootstrap admin password rejected: {weak.Message}");

            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            user = User.Create(normalized, "Platform Admin", scope.ServiceProvider.GetRequiredService<IClock>().UtcNow);
            user.SetPasswordHash(hasher.HashPassword(user, password));
            db.Add(user);
        }

        if (!user.IsPlatformAdmin)
        {
            user.GrantPlatformAdmin();
            logger.LogInformation("Granted platform admin to {UserId}", user.Id);
        }

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
