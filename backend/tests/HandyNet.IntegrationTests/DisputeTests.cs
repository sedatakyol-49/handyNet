using System.Net;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Ordering.Application;
using HandyNet.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class DisputeTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    /// <summary>A paid, shipped and delivered order — inside its inspection window.</summary>
    private async Task<(ApiClient Supplier, ApiClient Shop, string OrderId)> DeliveredPaidOrderAsync()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5);
        var orderId = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 1 } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() })).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        var checkout = new Uri((await shop.PostAsync($"/api/orders/{orderId}/payments")).Expect(HttpStatusCode.OK).Json["checkoutUrl"]!.GetValue<string>());
        var http = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        await http.PostAsync(checkout.AbsolutePath + "/complete", new FormUrlEncodedContent(new Dictionary<string, string> { ["outcome"] = "Paid" }));

        (await supplier.PostAsync($"/api/orders/{orderId}/confirm")).Expect(HttpStatusCode.OK);
        (await supplier.PostAsync($"/api/orders/{orderId}/ship")).Expect(HttpStatusCode.OK);
        (await shop.PostAsync($"/api/orders/{orderId}/confirm-delivery")).Expect(HttpStatusCode.OK);
        return (supplier, shop, orderId);
    }

    private static object Complaint => new { reason = "Damaged", description = "Display hat einen Riss, Fotos folgen." };

    private async Task ExpireInspectionAsync(string orderId)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var cmd = new NpgsqlCommand($"UPDATE ordering.orders SET inspection_ends_at = now() - interval '1 minute' WHERE id = '{orderId}'", owner);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> PaymentStatusAsync(ApiClient client, string orderId) =>
        (await client.GetAsync($"/api/orders/{orderId}/payments")).Json.AsArray().First()!["status"]!.GetValue<string>();

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
    public async Task Open_dispute_holds_completion_and_seller_refund_returns_the_money()
    {
        var (supplier, shop, orderId) = await DeliveredPaidOrderAsync();
        var dispute = (await shop.PostAsync($"/api/orders/{orderId}/disputes", Complaint)).Expect(HttpStatusCode.Created).Json;
        var disputeId = dispute["id"]!.GetValue<string>();
        Assert.Equal("Disputed", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());
        // A second complaint is refused (the order is no longer in Delivered).
        Assert.Equal("order.invalid_transition", (await shop.PostAsync($"/api/orders/{orderId}/disputes", Complaint)).Code);

        // The window passes, but a disputed order is not completed automatically.
        await ExpireInspectionAsync(orderId);
        await fixture.Factory.Services.GetRequiredService<OrderCompletionWorker>().RunOnceAsync(CancellationToken.None);
        Assert.Equal("Disputed", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());

        (await supplier.PostAsync($"/api/disputes/{disputeId}/messages", new { text = "Bitte senden Sie Fotos." })).Expect(HttpStatusCode.OK);
        Assert.Equal("dispute.not_seller", (await shop.PostAsync($"/api/disputes/{disputeId}/refund", new { text = "x" })).Code);

        var refunded = (await supplier.PostAsync($"/api/disputes/{disputeId}/refund", new { text = "Erstattung, Ware bitte entsorgen." })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Refunded", refunded["status"]!.GetValue<string>());
        Assert.Equal(2, refunded["messages"]!.AsArray().Count); // complaint + seller question; the decision is stored as resolution
        Assert.Equal("Refunded", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());
        await UntilAsync(async () => await PaymentStatusAsync(shop, orderId) == "Refunded");
        Assert.Equal(0, (await supplier.GetAsync("/api/payments/balance")).Json["openBalanceMinor"]!.GetValue<long>());
    }

    [Fact]
    public async Task Withdrawn_dispute_completes_the_order_and_pays_the_seller()
    {
        var (supplier, shop, orderId) = await DeliveredPaidOrderAsync();
        var disputeId = (await shop.PostAsync($"/api/orders/{orderId}/disputes", Complaint)).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.PostAsync($"/api/disputes/{disputeId}/withdraw", new { text = "x" })).Status);
        (await shop.PostAsync($"/api/disputes/{disputeId}/withdraw", new { text = "Hat sich geklärt." })).Expect(HttpStatusCode.OK);

        Assert.Equal("Completed", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());
        await UntilAsync(async () => (await supplier.GetAsync("/api/payments/balance")).Json["recentPayouts"]!.AsArray().Count == 1);
    }

    [Fact]
    public async Task Escalated_dispute_is_decided_by_the_platform()
    {
        var (supplier, shop, orderId) = await DeliveredPaidOrderAsync();
        var disputeId = (await shop.PostAsync($"/api/orders/{orderId}/disputes", Complaint)).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        var admin = await _scenario.AdminAsync();
        Assert.Equal("dispute.not_escalated",
            (await admin.PostAsync($"/api/admin/disputes/{disputeId}/decide", new { refund = false, resolution = "x" })).Code);

        (await supplier.PostAsync($"/api/disputes/{disputeId}/escalate", new { text = "Ware war beim Versand einwandfrei." })).Expect(HttpStatusCode.OK);
        var queue = (await admin.GetAsync("/api/admin/disputes")).Expect(HttpStatusCode.OK).Json.AsArray();
        var queued = queue.Single(d => d!["id"]!.GetValue<string>() == disputeId)!;
        Assert.StartsWith("B2B-", queued["orderNumber"]!.GetValue<string>());
        Assert.StartsWith("grosshandel", queued["sellerName"]!.GetValue<string>());
        Assert.StartsWith("handyshop", queued["buyerName"]!.GetValue<string>());
        var details = (await admin.GetAsync($"/api/admin/disputes/{disputeId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, details["messages"]!.AsArray().Count);
        Assert.Equal("platform", details["role"]!.GetValue<string>());

        var decided = (await admin.PostAsync($"/api/admin/disputes/{disputeId}/decide",
            new { refund = false, resolution = "Transportschaden nicht belegt." })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("ClosedWithoutRefund", decided["status"]!.GetValue<string>());
        Assert.Equal("Platform", decided["resolvedBy"]!.GetValue<string>());
        Assert.Equal("Transportschaden nicht belegt.", decided["resolution"]!.GetValue<string>());
        Assert.Equal(2, decided["messages"]!.AsArray().Count); // the decision is stored once, as the resolution
        Assert.Equal("Completed", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());

        // Non-admins cannot reach the platform queue.
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/admin/disputes")).Status);
    }

    [Fact]
    public async Task No_dispute_after_the_inspection_window_and_none_for_strangers()
    {
        var (_, shop, orderId) = await DeliveredPaidOrderAsync();
        var stranger = await _scenario.RegisterAsync("fremd", wholesaler: false);
        await _scenario.ApproveAsync(stranger, "B2BPurchasing");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/orders/{orderId}/disputes", Complaint)).Status);

        await ExpireInspectionAsync(orderId);
        Assert.Equal("order.inspection_over", (await shop.PostAsync($"/api/orders/{orderId}/disputes", Complaint)).Code);
    }
}
