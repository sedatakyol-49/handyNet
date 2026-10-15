using System.Net;
using HandyNet.IntegrationTests.Infrastructure;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class ConcurrencyTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    [Fact]
    public async Task Concurrent_checkouts_never_oversell()
    {
        const int stock = 5;
        const int attempts = 25;
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var otherShop = await _scenario.RegisterAsync("zweitshop", wholesaler: false);
        await _scenario.ApproveAsync(otherShop, "B2BPurchasing");
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: stock);

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(i =>
        {
            var buyer = i % 2 == 0 ? shop : otherShop;
            return buyer.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 1 } } },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"race-{i}" });
        }));

        Assert.Equal(stock, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal("order.insufficient_stock", r.Code));

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT s.on_hand, s.reserved,
                   (SELECT count(*) FROM inventory.stock_reservations r WHERE r.warehouse_id = s.warehouse_id AND r.product_id = s.product_id AND r.status = 'Active')
            FROM inventory.stock_levels s JOIN marketplace.offers o ON o.warehouse_id = s.warehouse_id AND o.product_id = s.product_id
            WHERE o.id = '{offerId}'
            """, owner);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(stock, reader.GetInt32(0));
        Assert.Equal(stock, reader.GetInt32(1));
        Assert.Equal(stock, reader.GetInt64(2));
    }

    [Fact]
    public async Task Concurrent_retries_with_the_same_idempotency_key_create_one_order()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 2 } } },
                new Dictionary<string, string> { ["Idempotency-Key"] = "same-key" })));

        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Body?.ToJsonString()));
        Assert.Single(results.Select(r => r.Json["id"]!.GetValue<string>()).Distinct());

        var listing = (await shop.GetAsync($"/api/marketplace/offers/{offerId}")).Expect(HttpStatusCode.OK);
        Assert.Equal(8, listing.Json["available"]!.GetValue<int>()); // reserved exactly once
    }
}
