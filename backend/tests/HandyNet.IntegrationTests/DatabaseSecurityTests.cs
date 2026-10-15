using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Talks to PostgreSQL directly as the application role to prove that isolation does not depend on
/// application code remembering a WHERE clause.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class DatabaseSecurityTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private async Task<NpgsqlConnection> ConnectAsAppAsync(Guid? organizationId)
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection);
        set.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        await set.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Row_level_security_hides_other_tenants_rows()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        await _scenario.PublishOfferAsync(supplier, shop, quantity: 3);

        await using (var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId))
        {
            Assert.True(await CountAsync(asSupplier, "SELECT count(*) FROM inventory.stock_levels") > 0);
            Assert.True(await CountAsync(asSupplier, "SELECT count(*) FROM marketplace.offers") > 0);
            Assert.Equal(0, await CountAsync(asSupplier,
                $"SELECT count(*) FROM inventory.stock_levels WHERE organization_id <> '{supplier.OrganizationId}'"));
        }

        await using (var asShop = await ConnectAsAppAsync(shop.OrganizationId))
        {
            // Even an unfiltered query returns none of the supplier's rows.
            Assert.Equal(0, await CountAsync(asShop, $"SELECT count(*) FROM inventory.stock_levels WHERE organization_id = '{supplier.OrganizationId}'"));
            Assert.Equal(0, await CountAsync(asShop, $"SELECT count(*) FROM marketplace.offers WHERE organization_id = '{supplier.OrganizationId}'"));
            Assert.Equal(0, await CountAsync(asShop, $"SELECT count(*) FROM imports.import_batches WHERE organization_id = '{supplier.OrganizationId}'"));
            // …but published offers are visible through the listing view.
            Assert.True(await CountAsync(asShop, $"SELECT count(*) FROM marketplace.offer_listings WHERE seller_organization_id = '{supplier.OrganizationId}'") > 0);
        }

        await using (var noTenant = await ConnectAsAppAsync(null))
        {
            Assert.Equal(0, await CountAsync(noTenant, "SELECT count(*) FROM inventory.stock_levels"));
            Assert.Equal(0, await CountAsync(noTenant, "SELECT count(*) FROM marketplace.offer_listings"));
        }
    }

    [Fact]
    public async Task Cannot_write_rows_for_another_tenant()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);

        await using var asShop = await ConnectAsAppAsync(shop.OrganizationId);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO inventory.warehouses (id, organization_id, code, name, is_default, created_at) VALUES (gen_random_uuid(), '{supplier.OrganizationId}', 'X', 'X', false, now())",
            asShop);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // new row violates row-level security policy

        await using var update = new NpgsqlCommand(
            $"UPDATE inventory.stock_levels SET on_hand = 999 WHERE organization_id = '{supplier.OrganizationId}'", asShop);
        Assert.Equal(0, await update.ExecuteNonQueryAsync()); // invisible rows cannot be updated
    }

    [Fact]
    public async Task History_tables_are_append_only_and_data_cannot_be_deleted()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        await _scenario.PublishOfferAsync(supplier, shop, quantity: 1);
        await using var asSupplier = await ConnectAsAppAsync(supplier.OrganizationId);

        foreach (var sql in new[]
                 {
                     "UPDATE inventory.stock_movements SET quantity = 100",
                     "DELETE FROM inventory.stock_movements",
                     "UPDATE infra.audit_log SET action = 'x'",
                     "DELETE FROM infra.audit_log",
                     "DELETE FROM marketplace.offers",
                     "DELETE FROM catalog.products",
                     "DROP TABLE marketplace.offers",
                 })
        {
            await using var command = new NpgsqlCommand(sql, asSupplier);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState is "42501" or "42P01", $"{sql}: {ex.SqlState} {ex.MessageText}");
        }
    }

    [Fact]
    public async Task Changes_are_written_to_the_audit_log()
    {
        var (supplier, _) = await _scenario.SupplierAndShopAsync();
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        var count = await CountAsync(owner,
            $"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'Organization' AND entity_id = '{supplier.OrganizationId}' AND changes ? 'Status'");
        Assert.True(count >= 2, "registration and verification should both be audited");
    }
}
