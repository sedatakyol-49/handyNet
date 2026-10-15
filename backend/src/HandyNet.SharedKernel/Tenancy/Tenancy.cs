namespace HandyNet.SharedKernel.Tenancy;

/// <summary>
/// Business modules that a platform administrator can activate per organization.
/// Only implemented modules are listed; new modules are added when they ship.
/// Stored as text in the database — never rename a member without a data migration.
/// </summary>
public enum PlatformModule
{
    ProductImport,
    Inventory,
    B2BPurchasing,
    B2BSales,
}

public enum MembershipRole
{
    Owner,
    Manager,
    Staff,
}

public static class Permissions
{
    public const string OrganizationManage = "organization.manage";
    public const string ImportsManage = "imports.manage";
    public const string OffersManage = "offers.manage";
    public const string InventoryManage = "inventory.manage";
    public const string MarketplaceBrowse = "marketplace.browse";
    public const string OrdersRead = "orders.read";
    public const string OrdersPlace = "orders.place";
    public const string OrdersFulfil = "orders.fulfil";

    private static readonly string[] All =
        [OrganizationManage, ImportsManage, OffersManage, InventoryManage, MarketplaceBrowse, OrdersRead, OrdersPlace, OrdersFulfil];

    public static IReadOnlySet<string> ForRole(MembershipRole role) => role switch
    {
        MembershipRole.Owner => All.ToHashSet(),
        MembershipRole.Manager => All.Where(p => p != OrganizationManage).ToHashSet(),
        MembershipRole.Staff => new HashSet<string> { MarketplaceBrowse, OrdersRead, OrdersPlace },
        _ => new HashSet<string>(),
    };
}

/// <summary>
/// The organization the current request acts for. Populated only after the caller's membership
/// has been verified against the database — never directly from client input.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    bool IsVerified { get; }
    MembershipRole? Role { get; }
    IReadOnlySet<PlatformModule> Modules { get; }
    IReadOnlySet<string> Permissions { get; }

    Guid RequiredOrganizationId => OrganizationId
        ?? throw new InvalidOperationException("No tenant context for this request.");

    bool HasModule(PlatformModule module) => IsVerified && Modules.Contains(module);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? OrganizationId { get; private set; }
    public bool IsVerified { get; private set; }
    public MembershipRole? Role { get; private set; }
    public IReadOnlySet<PlatformModule> Modules { get; private set; } = new HashSet<PlatformModule>();
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();

    public void Set(Guid organizationId, bool isVerified, MembershipRole role, IReadOnlySet<PlatformModule> modules)
    {
        if (OrganizationId is not null)
            throw new InvalidOperationException("Tenant context is already set for this scope.");

        OrganizationId = organizationId;
        IsVerified = isVerified;
        Role = role;
        Modules = modules;
        Permissions = Tenancy.Permissions.ForRole(role);
    }

    /// <summary>
    /// For trusted background jobs acting on behalf of one organization (e.g. completing an order after its
    /// inspection window). Grants no endpoint permissions; it only scopes database access (RLS) to that tenant.
    /// </summary>
    public void SetSystemActor(Guid organizationId)
    {
        if (OrganizationId is not null)
            throw new InvalidOperationException("Tenant context is already set for this scope.");
        OrganizationId = organizationId;
        IsVerified = true;
        IsSystemActor = true;
    }

    public bool IsSystemActor { get; private set; }
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    bool IsPlatformAdmin { get; }
}

public static class HandyNetClaims
{
    public const string PlatformAdmin = "hn_admin";
    public const string OrganizationHeader = "X-Organization-Id";
    public const string PlatformAdminPolicy = "PlatformAdmin";
}
