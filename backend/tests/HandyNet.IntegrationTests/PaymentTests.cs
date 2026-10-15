using System.Net;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class PaymentTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private async Task<(ApiClient Supplier, ApiClient Shop, string OrderId)> OrderAsync()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5);
        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 3 } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() })).Expect(HttpStatusCode.Created);
        return (supplier, shop, order.Json["id"]!.GetValue<string>());
    }

    /// <summary>Simulates the buyer completing checkout on the provider page (which then triggers webhook processing).</summary>
    private async Task PayAsync(ApiClient shop, string orderId, string outcome = "Paid")
    {
        var payment = (await shop.PostAsync($"/api/orders/{orderId}/payments")).Expect(HttpStatusCode.OK);
        var checkout = new Uri(payment.Json["checkoutUrl"]!.GetValue<string>());
        var http = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await http.PostAsync(checkout.AbsolutePath + "/complete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["outcome"] = outcome }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/app/bestellungen/{orderId}", response.Headers.Location!.ToString()); // back to the order, not the list
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task UntilAsync(Func<Task<bool>> done)
    {
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached within 30 s.");
            await outbox.DispatchBatchAsync(CancellationToken.None);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Paid_order_is_paid_out_after_completion_with_commission_deducted()
    {
        var (supplier, shop, orderId) = await OrderAsync();
        await PayAsync(shop, orderId);

        var payments = (await shop.GetAsync($"/api/orders/{orderId}/payments")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal("Paid", payments.Single()!["status"]!.GetValue<string>());
        Assert.Null(payments.Single()!["commissionNetMinor"]); // buyer does not see the seller's commission

        // 3 × 89,90 = 269,70 net → 320,94 gross; 3 % commission 8,09 + 19 % VAT 1,54 → seller share 311,31
        var sellerView = (await supplier.GetAsync($"/api/orders/{orderId}/payments")).Json.AsArray().Single()!;
        Assert.Equal(31131, sellerView["sellerShareMinor"]!.GetValue<long>());
        Assert.Equal(31131, (await supplier.GetAsync("/api/payments/balance")).Json["openBalanceMinor"]!.GetValue<long>());

        (await supplier.PostAsync($"/api/orders/{orderId}/confirm")).Expect(HttpStatusCode.OK);
        (await supplier.PostAsync($"/api/orders/{orderId}/ship")).Expect(HttpStatusCode.OK);
        (await shop.PostAsync($"/api/orders/{orderId}/confirm-delivery")).Expect(HttpStatusCode.OK);
        (await shop.PostAsync($"/api/orders/{orderId}/accept")).Expect(HttpStatusCode.OK);

        await UntilAsync(async () => (await supplier.GetAsync("/api/payments/balance")).Json["recentPayouts"]!.AsArray().Count == 1);
        var balance = (await supplier.GetAsync("/api/payments/balance")).Json;
        Assert.Equal(0, balance["openBalanceMinor"]!.GetValue<long>());
        Assert.Equal(31131, balance["recentPayouts"]![0]!["amountMinor"]!.GetValue<long>());
        Assert.StartsWith("B2B-", balance["recentPayouts"]![0]!["orderNumber"]!.GetValue<string>());
        var ledger = (await supplier.GetAsync("/api/payments/ledger")).Json["items"]!.AsArray();
        Assert.All(ledger, l => Assert.Contains("B2B-", l!["description"]!.GetValue<string>()));

        // The whole ledger balances, and the platform keeps exactly commission + VAT at the PSP.
        Assert.Equal(0, await CountAsync("SELECT coalesce(sum(amount_minor), 0) FROM ledger.postings"));
        Assert.Equal(2, await CountAsync($"SELECT count(*) FROM ledger.journal_entries WHERE source_id IN (SELECT id FROM payments.payments WHERE order_id = '{orderId}' UNION SELECT id FROM payments.payouts WHERE order_id = '{orderId}')"));
    }

    [Fact]
    public async Task Cancelling_a_paid_order_refunds_the_buyer_and_reverses_the_ledger()
    {
        var (supplier, shop, orderId) = await OrderAsync();
        await PayAsync(shop, orderId);
        (await shop.PostAsync($"/api/orders/{orderId}/cancel", new { reason = "Doch nicht" })).Expect(HttpStatusCode.OK);

        await UntilAsync(async () =>
            (await shop.GetAsync($"/api/orders/{orderId}/payments")).Json.AsArray().Single()!["status"]!.GetValue<string>() == "Refunded");
        Assert.Equal(0, (await supplier.GetAsync("/api/payments/balance")).Json["openBalanceMinor"]!.GetValue<long>());
    }

    [Fact]
    public async Task Replayed_webhooks_and_double_checkout_do_not_charge_or_book_twice()
    {
        var (_, shop, orderId) = await OrderAsync();
        var first = (await shop.PostAsync($"/api/orders/{orderId}/payments")).Expect(HttpStatusCode.OK).Json;
        var second = (await shop.PostAsync($"/api/orders/{orderId}/payments")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(first["id"]!.GetValue<string>(), second["id"]!.GetValue<string>());

        await PayAsync(shop, orderId);
        var providerId = new Uri(first["checkoutUrl"]!.GetValue<string>()).Segments.Last();
        var anonymous = fixture.Factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var webhook = await anonymous.PostAsync("/api/payments/webhooks/simulated",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = providerId }));
            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        }

        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM ledger.journal_entries WHERE idempotency_key = 'payment:{first["id"]}:capture'"));
        Assert.Equal("payment.already_paid", (await shop.PostAsync($"/api/orders/{orderId}/payments")).Code);
    }

    [Fact]
    public async Task Ledger_cannot_be_written_or_changed_outside_the_posting_function()
    {
        var (supplier, shop, orderId) = await OrderAsync();
        await PayAsync(shop, orderId);

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_org', '{supplier.OrganizationId}', false)", app))
            await set.ExecuteNonQueryAsync();

        foreach (var sql in new[]
                 {
                     "INSERT INTO ledger.postings (journal_entry_id, account_id, amount_minor, currency) SELECT journal_entry_id, account_id, 1, 'EUR' FROM ledger.postings LIMIT 1",
                     "UPDATE ledger.postings SET amount_minor = 1",
                     "DELETE FROM ledger.journal_entries",
                     // Unbalanced entry through the function:
                     "SELECT ledger.post_entry('x1', 'd', 't', gen_random_uuid(), now(), '[{\"account_type\":\"PspClearing\",\"amount_minor\":100,\"currency\":\"EUR\"},{\"account_type\":\"CommissionRevenue\",\"amount_minor\":-99,\"currency\":\"EUR\"}]'::jsonb)",
                     // Moving money on another organization's seller account:
                     $"SELECT ledger.post_entry('x2', 'd', 't', gen_random_uuid(), now(), '[{{\"account_type\":\"PspClearing\",\"amount_minor\":-100,\"currency\":\"EUR\"}},{{\"account_type\":\"SellerPayable\",\"organization_id\":\"{shop.OrganizationId}\",\"amount_minor\":100,\"currency\":\"EUR\"}}]'::jsonb)",
                 })
        {
            await using var command = new NpgsqlCommand(sql, app);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState is "42501" or "22023", $"{sql}: {ex.SqlState} {ex.MessageText}");
        }

        // Even the owner role cannot edit history.
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var update = new NpgsqlCommand("UPDATE ledger.postings SET amount_minor = amount_minor", owner);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);

        // The supplier sees only its own accounts.
        await using var visible = new NpgsqlCommand("SELECT count(*) FROM ledger.accounts WHERE organization_id IS NULL OR organization_id <> infra.current_org()", app);
        Assert.Equal(0L, (long)(await visible.ExecuteScalarAsync())!);
    }
}
