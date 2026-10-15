using HandyNet.Modules.Catalog.Infrastructure;
using HandyNet.Modules.Identity.Infrastructure;
using HandyNet.Modules.Imports.Infrastructure;
using HandyNet.Modules.Inventory.Infrastructure;
using HandyNet.Modules.Marketplace.Infrastructure;
using HandyNet.Modules.Notifications.Infrastructure;
using HandyNet.Modules.Ordering.Infrastructure;
using HandyNet.Modules.Shipping.Infrastructure;
using HandyNet.Modules.Ledger.Infrastructure;
using HandyNet.Modules.Payments.Infrastructure;
using HandyNet.Modules.Disputes.Infrastructure;
using HandyNet.Modules.Ankauf.Infrastructure;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.Modules.Invoicing.Infrastructure;
using HandyNet.Modules.Tenancy.Infrastructure;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HandyNet.Migrations;

/// <summary>
/// Applies migrations with the schema-owner connection. The API itself connects with the restricted
/// application role (no DDL, RLS enforced), so migrations are a separate, explicit deployment step.
/// </summary>
public static class DatabaseMigrator
{
    public static IReadOnlyList<IModuleModel> AllModels { get; } =
    [
        new IdentityModel(),
        new TenancyModel(),
        new CatalogModel(),
        new InventoryModel(),
        new MarketplaceModel(),
        new ImportsModel(),
        new OrderingModel(),
        new NotificationsModel(),
        new ShippingModel(),
        new LedgerModel(),
        new PaymentsModel(),
        new DisputesModel(),
        new AnkaufModel(),
        new PosModel(),
        new InvoicingModel(),
    ];

    public static HandyNetDbContext CreateContext(string ownerConnectionString)
    {
        var options = new DbContextOptionsBuilder<HandyNetDbContext>();
        PersistenceRegistration.ConfigureNpgsql(options, ownerConnectionString);
        return new HandyNetDbContext(options.Options, AllModels);
    }

    public static async Task<IReadOnlyList<string>> MigrateAsync(string ownerConnectionString, CancellationToken ct = default)
    {
        await using var db = CreateContext(ownerConnectionString);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        await db.Database.MigrateAsync(ct);
        return pending;
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>. No database connection is needed to add a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<HandyNetDbContext>
{
    public HandyNetDbContext CreateDbContext(string[] args) =>
        DatabaseMigrator.CreateContext(Environment.GetEnvironmentVariable("HANDYNET_MIGRATIONS_CONNECTION")
                                       ?? "Host=localhost;Database=handynet_design;Username=postgres");
}
