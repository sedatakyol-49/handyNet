using System.Security.Claims;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace HandyNet.Modules.Identity.Application;

/// <summary>
/// Runs for every request with a valid access token (JWT bearer <c>OnTokenValidated</c>): a token of a disabled account, or a
/// platform-admin token whose admin role was revoked meanwhile, is rejected immediately (401) instead of working until it
/// expires. The client then refreshes, which fails for a disabled account and issues a token without the admin claim
/// otherwise. One primary-key lookup per authenticated request.
/// </summary>
public sealed class AccountStatusCheck(HandyNetDbContext db)
{
    /// <summary>Null when the token may be used, otherwise the reason it is rejected.</summary>
    public async Task<string?> RejectReasonAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            return "Token has no subject.";
        var user = await db.Set<User>().AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.IsPlatformAdmin })
            .FirstOrDefaultAsync(ct);
        if (user is null || !user.IsActive)
            return "The account is disabled.";
        if (principal.HasClaim(HandyNetClaims.PlatformAdmin, "true") && !user.IsPlatformAdmin)
            return "The platform administrator role was revoked.";
        return null;
    }
}
