using System.Net;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Ordering.Application;
using HandyNet.Modules.Shipping.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

[Collection(HandyNetCollection.Name)]
public sealed class ShippingTests(HandyNetFixture fixture)
{
    private readonly Scenario _scenario = new(fixture);

    private async Task<(ApiClient Supplier, ApiClient Shop, string OrderId)> ConfirmedOrderAsync(string price = "89,90", int quantity = 2)
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5, price: price);
        var order = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() })).Expect(HttpStatusCode.Created);
        var orderId = order.Json["id"]!.GetValue<string>();
        (await supplier.PostAsync($"/api/orders/{orderId}/confirm")).Expect(HttpStatusCode.OK);
        return (supplier, shop, orderId);
    }

    private static object Parcel(string orderId, string carrier = "Simulated", bool insured = false, string? tracking = null) =>
        new { orderId, carrier, weightGrams = 800, lengthCm = 30, widthCm = 20, heightCm = 10, insured, trackingNumber = tracking };

    [Fact]
    public async Task Label_handover_tracking_delivery_and_acceptance()
    {
        var (supplier, shop, orderId) = await ConfirmedOrderAsync();

        var shipment = (await supplier.PostAsync("/api/shipments", Parcel(orderId))).Expect(HttpStatusCode.Created);
        var shipmentId = shipment.Json["id"]!.GetValue<string>();
        Assert.Equal("LabelCreated", shipment.Json["status"]!.GetValue<string>());
        Assert.True(shipment.Json["containsLithiumBatteries"]!.GetValue<bool>() == false); // displays are not batteries/devices

        // A label is not a handover: the order is still only confirmed.
        var label = await fixture.Factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/shipments/{shipmentId}/label")
        {
            Headers = { { "Authorization", $"Bearer {supplier.Token}" }, { "X-Organization-Id", supplier.OrganizationId.ToString() } },
        });
        Assert.Equal("application/pdf", label.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", await label.Content.ReadAsStringAsync());
        Assert.Equal("Confirmed", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());

        (await supplier.PostAsync($"/api/shipments/{shipmentId}/handover")).Expect(HttpStatusCode.OK);
        Assert.Equal("Shipped", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());

        // Carrier tracking (simulated, zero transit time) delivers; buyer's inspection window starts.
        var synced = (await supplier.PostAsync($"/api/shipments/{shipmentId}/sync")).Expect(HttpStatusCode.OK);
        Assert.Equal("Delivered", synced.Json["status"]!.GetValue<string>());
        Assert.Equal(3, synced.Json["events"]!.AsArray().Count);
        (await supplier.PostAsync($"/api/shipments/{shipmentId}/sync")).Expect(HttpStatusCode.OK); // repeat is harmless

        var delivered = (await shop.GetAsync($"/api/orders/{orderId}")).Json;
        Assert.Equal("Delivered", delivered["status"]!.GetValue<string>());
        var deliveredAt = delivered["deliveredAt"]!.GetValue<DateTimeOffset>();
        Assert.Equal(deliveredAt.AddHours(48), delivered["inspectionEndsAt"]!.GetValue<DateTimeOffset>());

        // Buyer sees the shipment and tracking too.
        var forBuyer = (await shop.GetAsync($"/api/shipments?orderId={orderId}")).Expect(HttpStatusCode.OK);
        Assert.Single(forBuyer.Json.AsArray());

        var accepted = (await shop.PostAsync($"/api/orders/{orderId}/accept")).Expect(HttpStatusCode.OK);
        Assert.Equal("Completed", accepted.Json["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Orders_complete_automatically_after_the_inspection_window()
    {
        var (supplier, shop, orderId) = await ConfirmedOrderAsync();
        var shipmentId = (await supplier.PostAsync("/api/shipments", Parcel(orderId, "Manual", tracking: "MAN-" + Guid.NewGuid())))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        (await supplier.PostAsync($"/api/shipments/{shipmentId}/handover")).Expect(HttpStatusCode.OK);
        (await shop.PostAsync($"/api/orders/{orderId}/confirm-delivery")).Expect(HttpStatusCode.OK);

        var worker = fixture.Factory.Services.GetRequiredService<OrderCompletionWorker>();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Delivered", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());

        // Let the window expire (as the owner, outside the app) and run the job.
        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var expire = new NpgsqlCommand(
                $"UPDATE ordering.orders SET inspection_ends_at = now() - interval '1 minute' WHERE id = '{orderId}'", owner);
            await expire.ExecuteNonQueryAsync();
        }
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Completed", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tracking_worker_syncs_shipments_as_the_seller()
    {
        var (supplier, shop, orderId) = await ConfirmedOrderAsync();
        var shipmentId = (await supplier.PostAsync("/api/shipments", Parcel(orderId))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        (await supplier.PostAsync($"/api/shipments/{shipmentId}/handover")).Expect(HttpStatusCode.OK);

        await fixture.Factory.Services.GetRequiredService<TrackingSyncWorker>().RunOnceAsync(CancellationToken.None, minAgeMinutes: 0);
        Assert.Equal("Delivered", (await shop.GetAsync($"/api/orders/{orderId}")).Json["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Booked_shipment_blocks_shipping_or_rejecting_the_order_around_it()
    {
        var (supplier, _, orderId) = await ConfirmedOrderAsync();
        var shipmentId = (await supplier.PostAsync("/api/shipments", Parcel(orderId))).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        Assert.Equal("order.has_open_shipment", (await supplier.PostAsync($"/api/orders/{orderId}/ship")).Code);
        Assert.Equal("order.has_open_shipment", (await supplier.PostAsync($"/api/orders/{orderId}/reject", new { reason = "x" })).Code);

        (await supplier.PostAsync($"/api/shipments/{shipmentId}/cancel")).Expect(HttpStatusCode.OK);
        (await supplier.PostAsync($"/api/orders/{orderId}/reject", new { reason = "Nicht lieferbar" })).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Manual_shipment_follows_the_buyers_delivery_confirmation()
    {
        var (supplier, shop, orderId) = await ConfirmedOrderAsync();
        var shipmentId = (await supplier.PostAsync("/api/shipments", Parcel(orderId, "Manual", tracking: "MAN-" + Guid.NewGuid())))
            .Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();
        (await supplier.PostAsync($"/api/shipments/{shipmentId}/handover")).Expect(HttpStatusCode.OK);
        (await shop.PostAsync($"/api/orders/{orderId}/confirm-delivery")).Expect(HttpStatusCode.OK);

        var outbox = fixture.Factory.Services.GetRequiredService<HandyNet.Persistence.OutboxDispatcher>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string status;
        do
        {
            await outbox.DispatchBatchAsync(CancellationToken.None);
            status = (await shop.GetAsync($"/api/shipments?orderId={orderId}")).Json.AsArray().Single()!["status"]!.GetValue<string>();
            if (status != "Delivered")
                await Task.Delay(100);
        } while (status != "Delivered" && DateTime.UtcNow < deadline);
        Assert.Equal("Delivered", status);
    }

    [Fact]
    public async Task Carrier_profiles_expose_the_insurance_threshold()
    {
        var (supplier, _) = await _scenario.SupplierAndShopAsync();
        var profiles = (await supplier.GetAsync("/api/shipments/carrier-profiles")).Expect(HttpStatusCode.OK).Json.AsArray();
        Assert.Contains(profiles, p => p!["carrier"]!.GetValue<string>() == "Simulated" && p["standardLiabilityMinor"]!.GetValue<long>() == 50000);
    }

    [Fact]
    public async Task High_value_goods_must_be_insured()
    {
        var (supplier, _, orderId) = await ConfirmedOrderAsync(price: "400,00", quantity: 2); // 952 € gross
        var uninsured = await supplier.PostAsync("/api/shipments", Parcel(orderId, insured: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, uninsured.Status);
        Assert.Equal("shipment.insurance_required", uninsured.Code);
        (await supplier.PostAsync("/api/shipments", Parcel(orderId, insured: true))).Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Only_the_seller_ships_and_only_confirmed_orders()
    {
        var (supplier, shop) = await _scenario.SupplierAndShopAsync();
        var (offerId, _) = await _scenario.PublishOfferAsync(supplier, shop, quantity: 5);
        var orderId = (await shop.PostAsync("/api/orders", new { lines = new[] { new { offerId, quantity = 1 } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = "s-1" })).Expect(HttpStatusCode.Created).Json["id"]!.GetValue<string>();

        var notConfirmed = await supplier.PostAsync("/api/shipments", Parcel(orderId));
        Assert.Equal("shipment.order_not_confirmed", notConfirmed.Code);

        (await supplier.PostAsync($"/api/orders/{orderId}/confirm")).Expect(HttpStatusCode.OK);
        var byBuyer = await shop.PostAsync("/api/shipments", Parcel(orderId));
        Assert.Equal(HttpStatusCode.Forbidden, byBuyer.Status); // shop has no B2BSales module

        (await supplier.PostAsync("/api/shipments", Parcel(orderId))).Expect(HttpStatusCode.Created);
        Assert.Equal("shipment.exists", (await supplier.PostAsync("/api/shipments", Parcel(orderId))).Code);
    }
}
