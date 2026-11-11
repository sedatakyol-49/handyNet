using System.Net;
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

    // ---- POS completion (ADR-0020 … 0023): every new tenant table is isolated by RLS, not by application code ------------------

    private readonly PosSupport _pos = new(fixture);

    /// <summary>
    /// The owner of the rows sees them; another tenant and a session without tenant see none, cannot update them and cannot
    /// move own rows into the other tenant (WITH CHECK). <paramref name="foreignInsert"/> inserts a row for the other tenant.
    /// </summary>
    private async Task AssertIsolatedAsync(string table, Guid owner, Guid other, string? foreignInsert, bool deletable, string touch)
    {
        await using (var asOwner = await ConnectAsAppAsync(owner))
        {
            Assert.True(await CountAsync(asOwner, $"SELECT count(*) FROM {table}") > 0, $"{table}: the owner sees its rows");
            Assert.Equal(0, await CountAsync(asOwner, $"SELECT count(*) FROM {table} WHERE organization_id <> '{owner}'"));
        }
        await using (var asOther = await ConnectAsAppAsync(other))
        {
            Assert.Equal(0, await CountAsync(asOther, $"SELECT count(*) FROM {table} WHERE organization_id = '{owner}'"));
            await using var update = new NpgsqlCommand($"UPDATE {table} SET {touch} WHERE organization_id = '{owner}'", asOther);
            try
            {
                Assert.Equal(0, await update.ExecuteNonQueryAsync()); // invisible rows cannot be updated
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                // append-only table: not even the statement is allowed
            }
            if (foreignInsert is not null)
                Assert.Equal("42501", await PosSupport.TryAsync(asOther, foreignInsert)); // new row violates row-level security policy
            var delete = await PosSupport.TryAsync(asOther, $"DELETE FROM {table} WHERE organization_id = '{owner}'");
            Assert.True(deletable ? delete is null : delete == "42501", $"{table}: DELETE as another tenant gave {delete ?? "success"}");
        }
        await using (var asOwnerAgain = await ConnectAsAppAsync(owner))
        {
            Assert.True(await CountAsync(asOwnerAgain, $"SELECT count(*) FROM {table}") > 0, $"{table}: rows survived the other tenant");
            Assert.Equal("42501", await PosSupport.TryAsync(asOwnerAgain, $"TRUNCATE {table}"));
        }
        await using var noTenant = await ConnectAsAppAsync(null);
        Assert.Equal(0, await CountAsync(noTenant, $"SELECT count(*) FROM {table}"));
        await using var owned = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owned.OpenAsync();
        Assert.Equal(1, await CountAsync(owned,
            $"SELECT count(*) FROM pg_policies WHERE schemaname || '.' || tablename = '{table}' AND policyname = 'tenant_isolation' AND qual LIKE '%current_org%' AND with_check LIKE '%current_org%'"));
        Assert.Equal(1, await CountAsync(owned, $"SELECT count(*) FROM pg_class WHERE oid = '{table}'::regclass AND relrowsecurity"));
    }

    [Fact]
    public async Task Card_terminal_tables_are_tenant_isolated()
    {
        var a = await _pos.ShopAsync("terminal-a");
        var b = await _pos.ShopAsync("terminal-b");
        var registerA = await PosSupport.RegisterAsync(a);
        var registerB = await PosSupport.RegisterAsync(b);
        foreach (var (shop, register) in new[] { (a, registerA), (b, registerB) })
        {
            (await shop.PostAsync($"/api/pos/registers/{register}/terminals", new { name = "Simulation", connection = "Simulated" })).Expect(HttpStatusCode.Created);
            (await shop.PostAsync($"/api/pos/registers/{register}/card-transactions", new { kind = "Payment", amountMinor = 10_00 })).Expect(HttpStatusCode.Created);
        }
        var (orgA, orgB) = (a.OrganizationId!.Value, b.OrganizationId!.Value);

        await AssertIsolatedAsync("pos.terminal_profiles", orgA, orgB,
            $"INSERT INTO pos.terminal_profiles (id, organization_id, register_id, name, connection, is_default, created_at, updated_at) "
            + $"VALUES (gen_random_uuid(), '{orgA}', '{registerA}', 'Fremd', 'Manual', false, now(), now())",
            deletable: true, touch: "name = 'gekapert'");
        await AssertIsolatedAsync("pos.card_transactions", orgA, orgB,
            $"INSERT INTO pos.card_transactions (id, organization_id, register_id, is_test, kind, amount_minor, status, created_at, created_by) "
            + $"VALUES (gen_random_uuid(), '{orgA}', '{registerA}', false, 'Payment', 100, 'Pending', now(), gen_random_uuid())",
            deletable: false, touch: "status = 'Approved', completed_at = now()");

        // The API answers 404 for another tenant's profile and transaction (existence is not revealed).
        var profileA = (await a.GetAsync($"/api/pos/registers/{registerA}/terminals")).Expect(HttpStatusCode.OK).Json.AsArray().Single()!["id"]!.GetValue<string>();
        var transactionA = (await a.GetAsync("/api/pos/card-transactions")).Expect(HttpStatusCode.OK).Json["items"]!.AsArray().Single()!["id"]!.GetValue<string>();
        (await b.GetAsync($"/api/pos/terminals/{profileA}")).Expect(HttpStatusCode.NotFound);
        (await b.GetAsync($"/api/pos/registers/{registerA}/terminals")).Expect(HttpStatusCode.NotFound);
        (await b.GetAsync($"/api/pos/card-transactions/{transactionA}")).Expect(HttpStatusCode.NotFound);
        (await b.PostAsync($"/api/pos/card-transactions/{transactionA}/result", new { status = "Approved" })).Expect(HttpStatusCode.NotFound);
        (await b.PostAsync($"/api/pos/registers/{registerA}/card-transactions", new { kind = "Payment", amountMinor = 10_00 })).Expect(HttpStatusCode.NotFound);
        // A sale of B cannot use A's approved transaction.
        (await a.PostAsync($"/api/pos/card-transactions/{transactionA}/result", new { status = "Approved" })).Expect(HttpStatusCode.OK);
        var product = await PosSupport.StockProductAsync(b, 1);
        (await b.PostAsync($"/api/pos/registers/{registerB}/sessions", new { openingCashMinor = 0 })).Expect(HttpStatusCode.Created);
        var stolen = await b.PostAsync("/api/pos/sales", new
        {
            registerId = registerB, lines = new[] { new { productId = product, quantity = 1, unitPriceMinor = 10_00 } },
            payments = new[] { new { method = "Card", amountMinor = 10_00, cardTransactionId = transactionA } },
        }, PosSupport.Key());
        Assert.Equal((HttpStatusCode.Conflict, "pos.card_transaction_invalid"), (stolen.Status, stolen.Code));
    }
}
