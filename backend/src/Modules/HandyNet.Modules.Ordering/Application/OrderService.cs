using HandyNet.SharedKernel.Web;
using FluentValidation;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using HandyNet.Modules.Ordering.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Ordering.Application;

public sealed record PlaceOrderLine(Guid OfferId, int Quantity, long? ExpectedUnitPriceMinor);

public sealed record PlaceOrderRequest(IReadOnlyList<PlaceOrderLine> Lines, string? BuyerReference);

public sealed record ReasonRequest(string? Reason);

public sealed record OrderLineDto(
    int LineNumber,
    Guid OfferId,
    Guid ProductId,
    string SupplierSku,
    string Brand,
    string ProductName,
    int Quantity,
    long UnitPriceMinor,
    long LineNetMinor);

public sealed record OrderDto(
    Guid Id,
    string Number,
    string Status,
    string Role,
    Guid BuyerOrganizationId,
    string BuyerName,
    Guid SellerOrganizationId,
    string SellerName,
    string Currency,
    long NetTotalMinor,
    int VatRateBasisPoints,
    long VatTotalMinor,
    long GrossTotalMinor,
    string? BuyerReference,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? InspectionEndsAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    IReadOnlyList<OrderLineDto> Lines);

public sealed record PlaceOrderResult(OrderDto Order, bool Replayed);

internal sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderRequest>
{
    public const int MaxLines = 100;

    public PlaceOrderValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().MaxCount(l => l!.Count, MaxLines, $"At most {MaxLines} lines per order.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.OfferId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 100_000);
        });
        RuleFor(x => x.BuyerReference).MaximumLength(100);
    }
}

internal sealed class OrderService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IOfferReservations offers,
    IInventory inventory,
    IOrganizationDirectory organizations,
    IOptions<OrderingOptions> options,
    IEnumerable<IShipmentGate> shipmentGates,
    IClock clock,
    ILogger<OrderService> logger) : IOrderFulfilment
{
    public const int MaxIdempotencyKeyLength = 100;

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>
    /// Places an order and reserves stock for every line in one transaction. Retrying with the same
    /// Idempotency-Key returns the original order instead of creating a second one.
    /// </summary>
    public async Task<Result<PlaceOrderResult>> PlaceAsync(PlaceOrderRequest request, string idempotencyKey, CancellationToken ct)
    {
        var requestHash = HashRequest(request);
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } replay)
            return replay;

        var offerIds = request.Lines.Select(l => l.OfferId).Distinct().ToList();
        if (offerIds.Count != request.Lines.Count)
            return Error.Validation("order.duplicate_offer", "Each offer may appear only once per order.");

        var listings = await offers.GetListingsAsync(offerIds, ct);
        var missing = offerIds.FirstOrDefault(id => !listings.ContainsKey(id));
        if (missing != Guid.Empty)
            return await BlockedErrorAsync(missing, ct)
                   ?? Error.Validation("order.offer_unavailable", $"Offer {missing} is not available.");

        var sellers = listings.Values.Select(l => l.SellerOrganizationId).Distinct().ToList();
        if (sellers.Count != 1)
            return Error.Validation("order.multiple_sellers", "An order can contain offers from one seller only. Split the cart per seller.");

        var lines = new List<NewOrderLine>();
        foreach (var requested in request.Lines)
        {
            var listing = listings[requested.OfferId];
            if (requested.Quantity < listing.MinOrderQuantity)
                return Error.Validation("order.below_minimum",
                        $"{listing.ProductName}: minimum order quantity is {listing.MinOrderQuantity}.")
                    .WithDetails(new Dictionary<string, object?> { ["offerId"] = listing.OfferId, ["minOrderQuantity"] = listing.MinOrderQuantity });
            if (requested.ExpectedUnitPriceMinor is { } expected && expected != listing.PriceMinor)
                return Error.Conflict("order.price_changed",
                        $"The price of {listing.ProductName} changed to {listing.PriceMinor / 100m:0.00} {listing.Currency}. Review the cart.")
                    .WithDetails(new Dictionary<string, object?>
                    {
                        ["offerId"] = listing.OfferId,
                        ["expectedUnitPriceMinor"] = expected,
                        ["currentUnitPriceMinor"] = listing.PriceMinor,
                    });
            lines.Add(new NewOrderLine(listing.OfferId, listing.ProductId, listing.SupplierSku, listing.Brand, listing.ProductName,
                requested.Quantity, new Money(listing.PriceMinor, listing.Currency)));
        }

        var placed = Order.Place(OrganizationId, sellers[0], lines, idempotencyKey, requestHash, request.BuyerReference, currentUser.UserId, clock.UtcNow);
        if (!placed.IsSuccess)
            return placed.Error!;
        var order = placed.Value;

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Each reservation row-locks the offer's stock level until commit. Locking in one global order (product, then
            // offer) instead of the cart's order keeps two carts with the same items in another order from deadlocking.
            foreach (var line in order.Lines.OrderBy(l => l.ProductId).ThenBy(l => l.OfferId))
            {
                var reservation = await offers.ReserveAsync(line.OfferId, line.Quantity, $"order:{order.Id}", ct);
                if (reservation is null)
                {
                    await transaction.RollbackAsync(ct);
                    // Blocked by the platform between pricing and reservation.
                    if (await BlockedErrorAsync(line.OfferId, ct) is { } blocked)
                        return blocked;
                    var current = (await offers.GetListingsAsync([line.OfferId], ct)).GetValueOrDefault(line.OfferId);
                    var available = current?.Available ?? 0;
                    return Error.Conflict("order.insufficient_stock",
                            $"Not enough stock for {line.ProductName} (requested {line.Quantity}, available {available}).")
                        .WithDetails(new Dictionary<string, object?>
                        {
                            ["offerId"] = line.OfferId,
                            ["requestedQuantity"] = line.Quantity,
                            ["availableQuantity"] = available,
                        });
                }

                order.AttachReservation(line.LineNumber, reservation.Value);
            }

            db.Add(order);
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // A concurrent request with the same Idempotency-Key won the race. Our reservations roll back with the transaction.
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                logger.LogInformation("Concurrent duplicate order submission for key {Key}", idempotencyKey);
                return await ReplayAsync(idempotencyKey, requestHash, ct)
                       ?? Error.Conflict("order.duplicate", "The order could not be placed. Try again.");
            }
        }

        return new PlaceOrderResult(await ToDtoAsync(order, ct), Replayed: false);
    }

    /// <summary><c>offer.blocked</c> when the platform has blocked this offer (or its seller); null otherwise.</summary>
    private async Task<Error?> BlockedErrorAsync(Guid offerId, CancellationToken ct) =>
        (await offers.GetBlockedAsync([offerId], ct)).Contains(offerId)
            ? Error.Conflict("offer.blocked", "This offer has been removed from the marketplace by the platform and cannot be ordered.")
                .WithDetails(new Dictionary<string, object?> { ["offerId"] = offerId })
            : null;

    public async Task<PagedResult<OrderDto>> ListAsync(string? role, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsQueryable();
        query = role == "sales"
            ? query.Where(o => o.SellerOrganizationId == OrganizationId)
            : query.Where(o => o.BuyerOrganizationId == OrganizationId);
        if (Enum.TryParse<OrderStatus>(status, true, out var parsed))
            query = query.Where(o => o.Status == parsed);

        var total = await query.CountAsync(ct);
        var orders = await query.OrderByDescending(o => o.PlacedAt).Skip((p - 1) * size).Take(size).AsSplitQuery().ToListAsync(ct);
        return new PagedResult<OrderDto>(await ToDtosAsync(orders, ct), p, size, total);
    }

    public async Task<Result<OrderDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadAsync(id, track: false, ct);
        return order is null ? Error.NotFound("order.not_found", "Order not found.") : await ToDtoAsync(order, ct);
    }

    public Task<Result<OrderDto>> ConfirmAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, Party.Seller, order => Task.FromResult(order.Confirm(clock.UtcNow)), ct);

    /// <summary>Ships without a tracked shipment (e.g. own courier, pickup). Consumes the reserved stock.</summary>
    public Task<Result<OrderDto>> ShipAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, Party.Seller, async order =>
            await OpenShipmentBlocksAsync(order.Id, ct) ?? await ShipStockAsync(order, clock.UtcNow, ct), ct);

    public Task<Result<OrderDto>> RejectAsync(Guid id, string? reason, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reason)
            ? Task.FromResult<Result<OrderDto>>(Error.Validation("reason.required", "A reason is required."))
            : ActAsync(id, Party.Seller, async order =>
                await OpenShipmentBlocksAsync(order.Id, ct) ?? await CloseAsync(order, order.Reject(reason, clock.UtcNow), ct), ct);

    public Task<Result<OrderDto>> CancelAsync(Guid id, string? reason, CancellationToken ct) =>
        ActAsync(id, Party.Buyer, order => CloseAsync(order, order.Cancel(reason, clock.UtcNow), ct), ct);

    /// <summary>Buyer confirms receipt (no carrier tracking, or tracking lags behind).</summary>
    public Task<Result<OrderDto>> ConfirmDeliveryAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, Party.Buyer, order => Task.FromResult(order.MarkDelivered(clock.UtcNow, InspectionWindow, clock.UtcNow)), ct);

    /// <summary>Buyer accepts the goods within the inspection window.</summary>
    public Task<Result<OrderDto>> AcceptAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, Party.Buyer, order => Task.FromResult(order.Accept(clock.UtcNow)), ct);

    // ---- IOrderFulfilment (used by Shipping and background jobs; joins the caller's transaction) ----

    public async Task<FulfilmentOrder?> GetForFulfilmentAsync(Guid orderId, CancellationToken ct)
    {
        var order = await LoadAsync(orderId, track: false, ct);
        return order is null
            ? null
            : new FulfilmentOrder(order.Id, order.DisplayNumber, order.BuyerOrganizationId, order.SellerOrganizationId,
                order.Status.ToString(), order.NetTotalMinor, order.GrossTotalMinor, order.Lines.Select(l => l.ProductId).Distinct().ToList());
    }

    public async Task<Result> MarkShippedAsync(Guid orderId, DateTimeOffset shippedAt, CancellationToken ct) =>
        await ActAsync(orderId, Party.Seller, order => ShipStockAsync(order, shippedAt, ct), ct);

    public async Task<Result> MarkDeliveredAsync(Guid orderId, DateTimeOffset deliveredAt, CancellationToken ct) =>
        await ActAsync(orderId, Party.Either, order => Task.FromResult(order.MarkDelivered(deliveredAt, InspectionWindow, clock.UtcNow)), ct);

    public async Task<Result> CompleteAfterInspectionAsync(Guid orderId, CancellationToken ct) =>
        await ActAsync(orderId, Party.Either, order => Task.FromResult(order.CompleteAfterInspection(clock.UtcNow)), ct);

    public async Task<Result> OpenDisputeAsync(Guid orderId, CancellationToken ct) =>
        await ActAsync(orderId, Party.Buyer, order => Task.FromResult(order.OpenDispute(clock.UtcNow)), ct);

    public async Task<Result> CloseDisputeAsync(Guid orderId, bool refund, string reason, CancellationToken ct) =>
        await ActAsync(orderId, Party.Either, order => Task.FromResult(refund
            ? order.CloseDisputeWithRefund(reason, clock.UtcNow)
            : order.CloseDisputeWithoutRefund(clock.UtcNow)), ct);

    // ---- helpers ---------------------------------------------------------------------------------

    private TimeSpan InspectionWindow => TimeSpan.FromHours(options.Value.InspectionWindowHours);

    /// <summary>A booked shipment must be handed over (or cancelled) through Shipping, not bypassed here.</summary>
    private async Task<Result?> OpenShipmentBlocksAsync(Guid orderId, CancellationToken ct)
    {
        foreach (var gate in shipmentGates)
        {
            if (await gate.HasOpenShipmentAsync(orderId, ct))
                return Error.Conflict("order.has_open_shipment",
                    "This order has a booked shipment. Hand it over or cancel it in the shipping section first.");
        }
        return null;
    }

    private async Task<Result> ShipStockAsync(Order order, DateTimeOffset shippedAt, CancellationToken ct)
    {
        var shipped = order.MarkShipped(shippedAt);
        if (!shipped.IsSuccess)
            return shipped;
        foreach (var line in order.Lines)
        {
            var consumed = await inventory.StageConsumeReservationAsync(line.ReservationId!.Value, $"order:{order.Id}", ct);
            if (!consumed.IsSuccess)
                return consumed;
        }
        return Result.Success();
    }

    private async Task<Result> CloseAsync(Order order, Result transition, CancellationToken ct)
    {
        if (!transition.IsSuccess)
            return transition;
        foreach (var line in order.Lines.Where(l => l.ReservationId is not null))
        {
            if (!await inventory.ReleaseReservationAsync(line.ReservationId!.Value, ct))
                return Error.Conflict("reservation.not_active", $"Reservation for line {line.LineNumber} is no longer active.");
        }
        return Result.Success();
    }

    private enum Party
    {
        Buyer,
        Seller,
        Either,
    }

    /// <summary>
    /// Loads the order for the acting party, applies the action and saves. Starts its own transaction
    /// unless the caller (e.g. Shipping) already has one; then the caller commits or rolls back.
    /// </summary>
    private async Task<Result<OrderDto>> ActAsync(Guid id, Party party, Func<Order, Task<Result>> action, CancellationToken ct)
    {
        var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var order = await LoadAsync(id, track: true, ct);
            var allowed = order is not null && party switch
            {
                Party.Buyer => order.BuyerOrganizationId == OrganizationId,
                Party.Seller => order.SellerOrganizationId == OrganizationId,
                _ => true, // LoadAsync already restricts to buyer or seller
            };
            if (!allowed)
                return Error.NotFound("order.not_found", "Order not found.");

            var result = await action(order!);
            if (!result.IsSuccess)
            {
                if (ownTransaction is not null)
                    await ownTransaction.RollbackAsync(ct);
                return result.Error!;
            }

            await db.SaveChangesAsync(ct);
            if (ownTransaction is not null)
                await ownTransaction.CommitAsync(ct);
            return await ToDtoAsync(order!, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (ownTransaction is not null)
                await ownTransaction.RollbackAsync(ct);
            return Error.Conflict("concurrency", "The order was changed concurrently. Reload and try again.");
        }
        finally
        {
            if (ownTransaction is not null)
                await ownTransaction.DisposeAsync();
        }
    }

    private Task<Order?> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var query = db.Set<Order>().Include(o => o.Lines)
            .Where(o => o.Id == id && (o.BuyerOrganizationId == OrganizationId || o.SellerOrganizationId == OrganizationId));
        return (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns the earlier result for a reused Idempotency-Key, or an error when the key was used for a
    /// different request (a client bug that must not silently return an unrelated order).
    /// </summary>
    private async Task<Result<PlaceOrderResult>?> ReplayAsync(string key, string requestHash, CancellationToken ct)
    {
        var existing = await db.Set<Order>().AsNoTracking().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.BuyerOrganizationId == OrganizationId && o.IdempotencyKey == key, ct);
        if (existing is null)
            return null;
        if (existing.RequestHash != requestHash)
            return Error.Validation("order.idempotency_key_reused",
                    "This Idempotency-Key was already used for a different order request. Generate a new key for a new order.")
                .WithDetails(new Dictionary<string, object?> { ["orderId"] = existing.Id });
        return new PlaceOrderResult(await ToDtoAsync(existing, ct), Replayed: true);
    }

    /// <summary>Order-independent fingerprint of what was requested (offers, quantities, reference).</summary>
    internal static string HashRequest(PlaceOrderRequest request)
    {
        var canonical = string.Join("|", request.Lines.OrderBy(l => l.OfferId).Select(l => $"{l.OfferId:N}:{l.Quantity}"))
                        + "#" + (request.BuyerReference?.Trim() ?? string.Empty);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<OrderDto> ToDtoAsync(Order order, CancellationToken ct) => (await ToDtosAsync([order], ct))[0];

    private async Task<List<OrderDto>> ToDtosAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        var names = await organizations.GetAsync(
            orders.SelectMany(o => new[] { o.BuyerOrganizationId, o.SellerOrganizationId }).Distinct().ToList(), ct);
        return orders.Select(o => new OrderDto(
            o.Id, o.DisplayNumber, o.Status.ToString(), o.SellerOrganizationId == OrganizationId ? "seller" : "buyer",
            o.BuyerOrganizationId, names.GetValueOrDefault(o.BuyerOrganizationId)?.Name ?? "?",
            o.SellerOrganizationId, names.GetValueOrDefault(o.SellerOrganizationId)?.Name ?? "?",
            o.Currency, o.NetTotalMinor, o.VatRateBasisPoints, o.VatTotalMinor, o.GrossTotalMinor, o.BuyerReference,
            o.PlacedAt, o.ConfirmedAt, o.ShippedAt, o.DeliveredAt, o.InspectionEndsAt, o.CompletedAt, o.ClosedAt, o.CloseReason,
            o.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineDto(l.LineNumber, l.OfferId, l.ProductId, l.SupplierSku, l.Brand,
                l.ProductName, l.Quantity, l.UnitPriceMinor, l.LineNetMinor)).ToList())).ToList();
    }
}
