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
    Ankauf,

    /// <summary>Point of sale (Kassensystem) for the organization's own store sales.</summary>
    Pos,
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

    /// <summary>Sell at the register: open cash sessions, sales, lookup, products, customers, reprints.</summary>
    public const string PosSell = "pos.sell";

    /// <summary>Manage registers and POS prices, cancel whole receipts (Storno), date-range reports.</summary>
    public const string PosManage = "pos.manage";

    /// <summary>Sell a product at a price other than its POS price.</summary>
    public const string PosPriceOverride = "pos.price_override";

    /// <summary>Give line discounts, up to the membership's maximum discount percentage.</summary>
    public const string PosDiscount = "pos.discount";

    /// <summary>Take back sold items (returns with refund).</summary>
    public const string PosRefund = "pos.refund";

    /// <summary>Put cash into or take cash out of the drawer (Einlage/Entnahme).</summary>
    public const string PosCashMovement = "pos.cash_movement";

    /// <summary>Close a cash session (Tagesabschluss) and read its session report.</summary>
    public const string PosCloseSession = "pos.close_session";

    /// <summary>Read the organization's business reports (daily report: till, Ankauf, B2B purchases and sales). Owner and manager.</summary>
    public const string ReportsRead = "reports.read";

    /// <summary>POS rights an owner can grant or withhold per staff membership (per-cashier permissions).</summary>
    public static readonly IReadOnlySet<string> ConfigurablePos =
        new HashSet<string> { PosPriceOverride, PosDiscount, PosRefund, PosCashMovement, PosCloseSession };

    /// <summary>
    /// Staff POS rights while the membership has no own configuration: everything a cashier could do before the rights were
    /// split (discounts, returns, cash movements, closing). Selling at a price other than the POS price needs an explicit grant.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultStaffPos = new HashSet<string> { PosDiscount, PosRefund, PosCashMovement, PosCloseSession };

    public const int FullDiscountPercent = 100;

    private static readonly string[] All =
    [
        OrganizationManage, ImportsManage, OffersManage, InventoryManage, MarketplaceBrowse, OrdersRead, OrdersPlace, OrdersFulfil, PosSell, PosManage,
        PosPriceOverride, PosDiscount, PosRefund, PosCashMovement, PosCloseSession, ReportsRead,
    ];

    /// <summary>Default permissions of a role (for staff: a membership without own POS configuration).</summary>
    public static IReadOnlySet<string> ForRole(MembershipRole role) => role switch
    {
        MembershipRole.Owner => All.ToHashSet(),
        MembershipRole.Manager => All.Where(p => p != OrganizationManage).ToHashSet(),
        MembershipRole.Staff => new HashSet<string> { MarketplaceBrowse, OrdersRead, OrdersPlace, PosSell }.Concat(DefaultStaffPos).ToHashSet(),
        _ => new HashSet<string>(),
    };

    /// <summary>
    /// Effective permissions of a membership. Owners and managers always hold every POS right. For staff,
    /// <paramref name="posPermissions"/> (when configured) replaces the default POS rights; only <see cref="ConfigurablePos"/>
    /// entries count, so a stored configuration can never grant anything else.
    /// </summary>
    public static IReadOnlySet<string> For(MembershipRole role, IReadOnlyCollection<string>? posPermissions)
    {
        var permissions = ForRole(role);
        if (role != MembershipRole.Staff || posPermissions is null)
            return permissions;
        return permissions.Where(p => !ConfigurablePos.Contains(p)).Concat(posPermissions.Where(ConfigurablePos.Contains)).ToHashSet();
    }

    /// <summary>Largest line discount in percent: 100 for owners and managers; for staff the configured limit (default 100).</summary>
    public static int MaxDiscountPercentFor(MembershipRole role, int? configured) =>
        role == MembershipRole.Staff ? Math.Clamp(configured ?? FullDiscountPercent, 0, FullDiscountPercent) : FullDiscountPercent;
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

    /// <summary>Largest line discount (percent of the line amount before discount) the member may give at the POS.</summary>
    int MaxDiscountPercent { get; }

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
    public int MaxDiscountPercent { get; private set; }

    /// <param name="posPermissions">The membership's own POS rights (staff only); null = role defaults.</param>
    /// <param name="maxDiscountPercent">The membership's discount limit (staff only); null = role default.</param>
    public void Set(Guid organizationId, bool isVerified, MembershipRole role, IReadOnlySet<PlatformModule> modules,
        IReadOnlyCollection<string>? posPermissions = null, int? maxDiscountPercent = null)
    {
        if (OrganizationId is not null)
            throw new InvalidOperationException("Tenant context is already set for this scope.");

        OrganizationId = organizationId;
        IsVerified = isVerified;
        Role = role;
        Modules = modules;
        Permissions = Tenancy.Permissions.For(role, posPermissions);
        MaxDiscountPercent = Tenancy.Permissions.MaxDiscountPercentFor(role, maxDiscountPercent);
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
