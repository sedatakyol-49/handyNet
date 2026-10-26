using System.Net;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Notifications.Application;
using HandyNet.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class NotificationTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    /// <summary>
    /// Runs the outbox and email workers until <paramref name="done"/> holds. The background copies of the
    /// same workers run concurrently (and may hold rows via SKIP LOCKED), so we poll instead of draining once.
    /// </summary>
    private async Task ProcessUntilAsync(Func<bool> done)
    {
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        var email = fixture.Factory.Services.GetRequiredService<EmailDeliveryWorker>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "Expected emails were not delivered within 30 s.");
            await outbox.DispatchBatchAsync(CancellationToken.None);
            await email.DeliverBatchAsync(CancellationToken.None);
            await Task.Delay(100);
        }
    }

    private async Task<long> QueuedCountAsync(string recipient, string template)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.email_messages WHERE recipient = @r AND template = @t", owner);
        command.Parameters.AddWithValue("r", recipient);
        command.Parameters.AddWithValue("t", template);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Order_and_verification_emails_use_the_email_language_of_the_receiving_organization()
    {
        // Verification email: the organization chose English before the platform activated it.
        var supplier = await _scenario.RegisterAsync("sprache-lieferant", wholesaler: true);
        var shop = await _scenario.RegisterAsync("sprache-shop", wholesaler: false, retailer: true);
        await supplier.SetEmailLanguageAsync("en");
        await shop.SetEmailLanguageAsync("tr");
        await _scenario.ApproveAsync(supplier, "ProductImport", "Inventory", "B2BSales");
        await _scenario.ApproveAsync(shop, "B2BPurchasing", "Inventory");
        var supplierEmail = (await supplier.GetAsync("/api/me")).Json["email"]!.GetValue<string>();
        var shopEmail = (await shop.GetAsync("/api/me")).Json["email"]!.GetValue<string>();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5);

        // Order placed goes to the seller (English), the rejection to the buyer (Turkish).
        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 1 } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = "lang-1" })).Expect(HttpStatusCode.Created);
        (await supplier.PostAsync($"/api/orders/{order.Json["id"]!.GetValue<string>()}/reject", new { reason = "out of stock" })).Expect(HttpStatusCode.OK);
        await ProcessUntilAsync(() => fixture.Emails.Sent.Any(e => e.To == shopEmail && e.Subject == "Siparişiniz reddedildi")
                                      && fixture.Emails.Sent.Any(e => e.To == supplierEmail && e.Subject.StartsWith("New order from"))
                                      && fixture.Emails.Sent.Any(e => e.To == supplierEmail && e.Subject.EndsWith("has been activated"))
                                      && fixture.Emails.Sent.Any(e => e.To == shopEmail && e.Subject.EndsWith("etkinleştirildi")));
        Assert.DoesNotContain(fixture.Emails.Sent, e => (e.To == supplierEmail || e.To == shopEmail) && e.Subject.Contains("freigeschaltet"));
    }

    [Fact]
    public async Task Order_lifecycle_emails_reach_the_other_party_exactly_once()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var supplierEmail = (await supplier.GetAsync("/api/me")).Json["email"]!.GetValue<string>();
        var shopEmail = (await shop.GetAsync("/api/me")).Json["email"]!.GetValue<string>();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5);

        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 2 } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = "n-1" })).Expect(HttpStatusCode.Created);
        var orderId = order.Json["id"]!.GetValue<string>();
        (await supplier.PostAsync($"/api/orders/{orderId}/reject", new { reason = "Lieferant ausverkauft" })).Expect(HttpStatusCode.OK);
        await ProcessUntilAsync(() => fixture.Emails.Sent.Any(e => e.To == shopEmail && e.Subject.Contains("abgelehnt"))
                                      && fixture.Emails.Sent.Any(e => e.To == supplierEmail && e.Subject.StartsWith("Neue Bestellung"))
                                      && fixture.Emails.Sent.Any(e => e.To == supplierEmail && e.Subject.EndsWith("ist freigeschaltet")));

        var toSupplier = fixture.Emails.Sent.Where(e => e.To == supplierEmail).ToList();
        var toShop = fixture.Emails.Sent.Where(e => e.To == shopEmail).ToList();
        Assert.Contains(toSupplier, e => e.Subject.StartsWith("Neue Bestellung") && e.Body.Contains(orderId) && e.Body.Contains("€"));
        Assert.Contains(toShop, e => e.Subject == "Ihre Bestellung wurde abgelehnt" && e.Body.Contains("Lieferant ausverkauft"));
        Assert.Contains(toSupplier, e => e.Subject.EndsWith("ist freigeschaltet"));

        // Re-delivering the same outbox messages must not queue duplicates.
        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var reset = new NpgsqlCommand("UPDATE infra.outbox_messages SET processed_at = NULL", owner);
            await reset.ExecuteNonQueryAsync();
        }
        await ProcessUntilAsync(() => true);
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 5; i++)
        {
            await outbox.DispatchBatchAsync(CancellationToken.None);
            await Task.Delay(200);
        }
        Assert.Equal(1, await QueuedCountAsync(supplierEmail, "order.placed"));
        Assert.Equal(1, await QueuedCountAsync(shopEmail, "order.rejected"));
    }
}
