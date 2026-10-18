using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>
/// A product line (ProductId, any quantity) or a serialized device (Imei, quantity 1). Prices are gross, in cents. A unit
/// price other than the product's POS price needs <c>pos.price_override</c>; a discount needs <c>pos.discount</c> within the
/// cashier's limit. Note: free text printed under the line (max 200).
/// </summary>
public sealed record SaleLineRequest(Guid? ProductId, string? Imei, int Quantity, long UnitPriceMinor, long? DiscountMinor, string? Note = null);

public sealed record SalePaymentRequest(PaymentMethod Method, long AmountMinor);

public sealed record CreateSaleRequest(Guid RegisterId, IReadOnlyList<SaleLineRequest> Lines, IReadOnlyList<SalePaymentRequest> Payments,
    Guid? CustomerId = null);

/// <summary>Order history filters (all optional, combined with AND). Dates are inclusive from, exclusive to.</summary>
public sealed record SaleQuery(
    Guid? RegisterId,
    Guid? SessionId,
    long? Number,
    DateTimeOffset? From,
    DateTimeOffset? To,
    long? MinTotalMinor,
    long? MaxTotalMinor,
    Guid? CashierId,
    Guid? CustomerId,
    string? PaymentMethod,
    string? Kind,
    int? Page,
    int? PageSize);

/// <summary>RegisterId: the register (with an open session) issuing the storno; defaults to the original receipt's register.</summary>
public sealed record StornoRequest(string Reason, Guid? RegisterId);

public sealed record ReturnLineRequest(int LineNumber, int Quantity);

public sealed record ReturnRequest(IReadOnlyList<ReturnLineRequest> Lines, PaymentMethod RefundMethod, string Reason, Guid? RegisterId);

public sealed record ReceiptLineDto(
    int LineNumber,
    Guid ProductId,
    Guid? StockUnitId,
    string? Imei,
    string Description,
    int Quantity,
    long UnitPriceMinor,
    long DiscountMinor,
    long LineGrossMinor,
    string TaxTreatment,
    int VatRateBasisPoints,
    long? VatMinor,
    int? OriginalLineNumber,
    int ReturnedQuantity,
    string? Note);

public sealed record ReceiptPaymentDto(string Method, long AmountMinor, long? TenderedMinor);

public sealed record ReceiptDto(
    Guid Id,
    Guid RegisterId,
    string RegisterName,
    Guid SessionId,
    long Number,
    string Kind,
    Guid? ReferencedReceiptId,
    long? ReferencedReceiptNumber,
    Guid? CancelledByReceiptId,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    string? CreatedByName,
    string Currency,
    long TotalGrossMinor,
    long ChangeMinor,
    string? Reason,
    IReadOnlyList<ReceiptLineDto> Lines,
    IReadOnlyList<ReceiptPaymentDto> Payments,
    IReadOnlyList<DocumentVat> Vat,
    FiscalData? Fiscal,
    ReceiptDocument Document,
    Guid? CustomerId,
    DocumentCustomer? Customer,
    int ReprintCount,
    DateTimeOffset? LastReprintedAt,
    ApprovalInfoDto? Approval = null,
    long RoundingMinor = 0);

public sealed record ReceiptSummaryDto(Guid Id, Guid RegisterId, Guid SessionId, long Number, string Kind, Guid? ReferencedReceiptId,
    DateTimeOffset CreatedAt, long TotalGrossMinor, Guid CreatedBy, string? CreatedByName, Guid? CustomerId, string? CustomerName,
    IReadOnlyList<string> PaymentMethods, int ReprintCount);

/// <summary>A recorded reprint: the copy number to print ("Kopie n") and the unchanged original receipt.</summary>
public sealed record ReprintDto(int Sequence, DateTimeOffset PrintedAt, Guid PrintedBy, ReceiptDto Receipt);

public sealed record SaleResult(ReceiptDto Receipt, bool Replayed);

/// <summary>
/// Kind = "Product" (EAN) or "Unit" (IMEI). Stock figures refer to the register's warehouse (null without registerId).
/// RequiresImei: the product is stocked as serialized devices in that warehouse, so the IMEI must be scanned instead.
/// </summary>
public sealed record LookupDto(
    string Kind,
    Guid ProductId,
    string Brand,
    string Name,
    string? Ean,
    string? Imei,
    Guid? StockUnitId,
    string? TaxTreatment,
    Guid? WarehouseId,
    int? OnHand,
    int? Reserved,
    int? Available,
    bool RequiresImei,
    long? PriceMinor = null,
    string PriceSource = "None");

internal sealed class CreateSaleValidator : AbstractValidator<CreateSaleRequest>
{
    public CreateSaleValidator()
    {
        RuleFor(x => x.RegisterId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty().Must(l => l is null || l.Count <= Receipt.MaxLines).WithMessage($"At most {Receipt.MaxLines} lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l).Must(l => l.ProductId is not null || !string.IsNullOrWhiteSpace(l.Imei))
                .WithName("Line").WithMessage("Each line needs a productId or an IMEI.");
            line.RuleFor(l => l.Imei).Must(i => i is null || (i.Trim().Length == 15 && i.Trim().All(char.IsAsciiDigit)))
                .WithMessage("IMEI must have 15 digits.");
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, Receipt.MaxQuantity);
            line.RuleFor(l => l.Quantity).Equal(1).When(l => !string.IsNullOrWhiteSpace(l.Imei)).WithMessage("A device (IMEI) is sold with quantity 1.");
            line.RuleFor(l => l.UnitPriceMinor).InclusiveBetween(1, PosLimits.MaxAmountMinor);
            line.RuleFor(l => l.DiscountMinor).GreaterThanOrEqualTo(0).When(l => l.DiscountMinor is not null);
            line.RuleFor(l => l.Note).MaximumLength(Receipt.MaxNoteLength);
        });
        RuleFor(x => x.CustomerId).NotEqual(Guid.Empty).When(x => x.CustomerId is not null);
        RuleFor(x => x.Payments).NotNull().Must(p => p is null || p.Count <= 10).WithMessage("At most 10 payments.");
        RuleForEach(x => x.Payments).ChildRules(p =>
        {
            p.RuleFor(x => x.Method).IsInEnum();
            p.RuleFor(x => x.AmountMinor).InclusiveBetween(1, PosLimits.MaxAmountMinor);
        });
    }
}

internal sealed class StornoValidator : AbstractValidator<StornoRequest>
{
    public StornoValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(Receipt.MaxReasonLength);
}

internal sealed class ReturnValidator : AbstractValidator<ReturnRequest>
{
    public ReturnValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.LineNumber).GreaterThan(0);
            l.RuleFor(x => x.Quantity).InclusiveBetween(1, Receipt.MaxQuantity);
        });
        RuleFor(x => x.Lines).Must(l => l is null || l.Select(x => x.LineNumber).Distinct().Count() == l.Count)
            .WithMessage("Each line may appear only once.");
        RuleFor(x => x.RefundMethod).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(Receipt.MaxReasonLength);
    }
}

internal sealed class SaleService(
    HandyNetDbContext db,
    ITenantContext tenant,
    IInventory inventory,
    IProductCatalog catalog,
    IOrganizationDirectory organizations,
    IUserAccounts users,
    FiscalSealer fiscal,
    PriceResolver prices,
    IClock clock,
    ILogger<SaleService> logger,
    PosActor actor,
    ApprovalService approvals)
{
    public const int MaxIdempotencyKeyLength = 100;

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>
    /// One transaction: receipt number (register row lock) → open session (shared lock) → stock (reserved stock is never
    /// sold; IMEI units InStock → Sold) → VAT and payments → fiscal signature → immutable receipt. Any failure rolls back
    /// everything, including the receipt number. Retrying with the same Idempotency-Key returns the original receipt.
    /// </summary>
    public async Task<Result<SaleResult>> CreateSaleAsync(CreateSaleRequest request, string idempotencyKey, CancellationToken ct)
    {
        var requestHash = HashRequest(request);
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } replay)
            return replay;
        if (await fiscal.CheckAsync(request.RegisterId, ct) is { } fiscalRefused)
            return fiscalRefused;

        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RegisterId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (actor.CheckRegister(register.Id) is { } otherRegister)
            return otherRegister;
        DocumentCustomer? customer = null;
        if (request.CustomerId is { } customerId)
        {
            var found = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId && c.OrganizationId == OrganizationId, ct);
            if (found is null)
                return Error.NotFound("pos.customer_not_found", "Customer not found.");
            customer = found.ToDocument();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await PosSql.AllocateReceiptNumberAsync(db, register.Id, ct) is not { } number)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        // A concurrent request with the same key held the register lock until it committed: return its receipt.
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } concurrentReplay)
            return concurrentReplay;
        if (await PosSql.LockOpenSessionAsync(db, register.Id, exclusive: false, ct) is not { } sessionId)
            return Error.Conflict("pos.no_open_session", "Open a cash session on this register before selling.");

        var reference = $"pos:{register.Id}:{number}";
        var staged = new List<(SaleLineRequest Request, UnitSnapshot? Unit)>();
        foreach (var line in request.Lines)
        {
            if (!string.IsNullOrWhiteSpace(line.Imei))
            {
                var unit = await inventory.StageSellUnitAsync(register.WarehouseId, line.Imei.Trim(), reference, ct);
                if (!unit.IsSuccess)
                    return unit.Error!;
                if (line.ProductId is { } expected && expected != unit.Value.ProductId)
                    return Error.Validation("pos.imei_product_mismatch", $"The device {line.Imei} is not of the selected product.");
                staged.Add((line, unit.Value));
            }
            else
            {
                var sold = await inventory.StageSellAsync(register.WarehouseId, line.ProductId!.Value, line.Quantity, reference, ct);
                if (!sold.IsSuccess)
                    return sold.Error!;
                staged.Add((line, null));
            }
        }

        var productIds = staged.Select(s => s.Unit?.ProductId ?? s.Request.ProductId!.Value).Distinct().ToList();
        var products = await catalog.GetAsync(productIds, ct);
        if (productIds.FirstOrDefault(id => !products.ContainsKey(id)) is var missing && missing != Guid.Empty)
            return Error.NotFound("pos.product_not_found", $"Product {missing} not found.");

        // Per-cashier rights: price other than the till price, discount and discount limit (403 with a stable code).
        var tillPrices = await prices.ResolveAsync(register.WarehouseId, productIds, ct);
        var rights = Rights();
        for (var i = 0; i < staged.Count; i++)
        {
            var (line, unit) = staged[i];
            var allowed = LinePolicy.Check(i + 1, line.UnitPriceMinor, line.Quantity, line.DiscountMinor ?? 0,
                tillPrices[unit?.ProductId ?? line.ProductId!.Value].Price, rights);
            if (!allowed.IsSuccess)
                return allowed.Error!;
        }

        var inputs = staged.Select(s =>
        {
            var productId = s.Unit?.ProductId ?? s.Request.ProductId!.Value;
            var product = products[productId];
            var margin = s.Unit?.TaxScheme == TaxScheme.Margin25a;
            return new SaleLineInput(productId, s.Unit?.UnitId, s.Unit?.Imei, $"{product.Brand} {product.Name}", s.Request.Quantity,
                s.Request.UnitPriceMinor, s.Request.DiscountMinor ?? 0, margin ? TaxTreatment.Margin25a : TaxTreatment.Standard19,
                margin ? s.Unit!.PurchasePriceMinor : null, s.Request.Note);
        }).ToList();

        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, register.WarehouseId, number, actor.UserId, clock.UtcNow,
            idempotencyKey, requestHash, customer);
        var receipt = Receipt.CreateSale(header, inputs, request.Payments.Select(p => new PaymentInput(p.Method, p.AmountMinor)).ToList(),
            register.CashRounding);
        if (!receipt.IsSuccess)
            return receipt.Error!;

        var sealedResult = await SealAsync(receipt.Value, register.Name, null, ct);
        if (!sealedResult.IsSuccess)
            return sealedResult.Error!;

        db.Add(receipt.Value);
        try
        {
            await db.SaveChangesAsync(ct);
            if (await approvals.ConsumeAsync(actor, ApprovalUse.Sale, receipt.Value.Id, null, null, ct) is { } approvalError)
                return approvalError; // disposing the transaction rolls back the receipt and its number
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ix_receipts_organization_id_idempotency_key"))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            logger.LogInformation("Concurrent duplicate POS sale for key {Key}", idempotencyKey);
            return await ReplayAsync(idempotencyKey, requestHash, ct) ?? Error.Conflict("pos.duplicate", "The sale could not be recorded. Try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("pos.stock_changed", "Stock changed while selling (e.g. the same device or the last units were sold at another register). Scan again.");
        }

        return new SaleResult(await ToDtoAsync(receipt.Value, ct), Replayed: false);
    }

    /// <summary>Cancels a whole sale with a new negative receipt; payments are mirrored and the stock (and IMEI units) goes back.</summary>
    public async Task<Result<ReceiptDto>> StornoAsync(Guid receiptId, StornoRequest request, CancellationToken ct)
    {
        if (await fiscal.CheckAsync(request.RegisterId, ct) is { } fiscalRefused)
            return fiscalRefused;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOriginalAsync(receiptId, ct);
        var original = await LoadAsync(receiptId, ct);
        if (original is null)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        if (await StornoOfAsync(original.Id, ct) is { } existing)
            return Error.Conflict("pos.already_cancelled", "This receipt has already been cancelled.")
                .WithDetails(new Dictionary<string, object?> { ["stornoReceiptId"] = existing });
        var hasReturns = await db.Set<Receipt>().AnyAsync(r => r.ReferencedReceiptId == original.Id && r.Kind == ReceiptKind.Return, ct);

        var issuing = await IssuingRegisterAsync(request.RegisterId ?? original.RegisterId, ct);
        if (!issuing.IsSuccess)
            return issuing.Error!;
        var (register, number, sessionId) = issuing.Value;

        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, original.WarehouseId, number, actor.UserId, clock.UtcNow);
        var storno = Receipt.CreateStorno(header, original, hasReturns, request.Reason);
        if (!storno.IsSuccess)
            return storno.Error!;

        return await CompleteRefundAsync(tx, storno.Value, original, register, ct);
    }

    /// <summary>Returns selected quantities of a sale's lines with a new negative receipt; refunded by the given method.</summary>
    public async Task<Result<SaleResult>> ReturnAsync(Guid receiptId, ReturnRequest request, string idempotencyKey, CancellationToken ct)
    {
        var requestHash = HashReturn(receiptId, request);
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } replay)
            return replay;
        if (await fiscal.CheckAsync(request.RegisterId, ct) is { } fiscalRefused)
            return fiscalRefused;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOriginalAsync(receiptId, ct);
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } concurrentReplay)
            return concurrentReplay;
        var original = await LoadAsync(receiptId, ct);
        if (original is null)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        if (await StornoOfAsync(original.Id, ct) is not null)
            return Error.Conflict("pos.already_cancelled", "This receipt has been cancelled; nothing can be returned.");
        var alreadyReturned = await ReturnedQuantitiesAsync(original.Id, ct);

        var issuing = await IssuingRegisterAsync(request.RegisterId ?? original.RegisterId, ct);
        if (!issuing.IsSuccess)
            return issuing.Error!;
        var (register, number, sessionId) = issuing.Value;

        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, original.WarehouseId, number, actor.UserId, clock.UtcNow,
            idempotencyKey, requestHash);
        var returned = Receipt.CreateReturn(header, original, request.Lines.ToDictionary(l => l.LineNumber, l => l.Quantity), alreadyReturned,
            request.RefundMethod, request.Reason, register.CashRounding);
        if (!returned.IsSuccess)
            return returned.Error!;

        var completed = await CompleteRefundAsync(tx, returned.Value, original, register, ct);
        return completed.IsSuccess ? new SaleResult(completed.Value, Replayed: false) : completed.Error!;
    }

    public async Task<Result<ReceiptDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var receipt = await LoadAsync(id, ct);
        return receipt is null ? Error.NotFound("pos.receipt_not_found", "Receipt not found.") : await ToDtoAsync(receipt, ct);
    }

    /// <summary>Order history with filters; newest first.</summary>
    public async Task<Result<PagedResult<ReceiptSummaryDto>>> ListAsync(SaleQuery q, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = db.Set<Receipt>().AsNoTracking().Where(r => r.OrganizationId == OrganizationId);
        if (q.RegisterId is { } register)
            query = query.Where(r => r.RegisterId == register);
        if (q.SessionId is { } session)
            query = query.Where(r => r.SessionId == session);
        if (q.Number is { } number)
            query = query.Where(r => r.Number == number);
        if (q.From is { } from && q.To is { } to && from >= to)
            return Error.Validation("pos.date_range", "'from' must be before 'to'.");
        if (q.From?.ToUniversalTime() is { } fromUtc)
            query = query.Where(r => r.CreatedAt >= fromUtc);
        if (q.To?.ToUniversalTime() is { } toUtc)
            query = query.Where(r => r.CreatedAt < toUtc);
        if (q.MinTotalMinor is { } min && q.MaxTotalMinor is { } max && min > max)
            return Error.Validation("pos.amount_range", "'minTotalMinor' must not exceed 'maxTotalMinor'.");
        if (q.MinTotalMinor is { } minTotal)
            query = query.Where(r => r.TotalGrossMinor >= minTotal);
        if (q.MaxTotalMinor is { } maxTotal)
            query = query.Where(r => r.TotalGrossMinor <= maxTotal);
        if (q.CashierId is { } cashier)
            query = query.Where(r => r.CreatedBy == cashier);
        if (q.CustomerId is { } customerId)
            query = query.Where(r => r.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(q.PaymentMethod))
        {
            if (!Enum.TryParse<PaymentMethod>(q.PaymentMethod.Trim(), ignoreCase: true, out var method) || !Enum.IsDefined(method))
                return Error.Validation("pos.payment_method", $"Unknown payment method '{q.PaymentMethod}'.");
            query = query.Where(r => db.Set<ReceiptPayment>().Any(x => x.ReceiptId == r.Id && x.Method == method));
        }
        if (!string.IsNullOrWhiteSpace(q.Kind))
        {
            if (!Enum.TryParse<ReceiptKind>(q.Kind.Trim(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                return Error.Validation("pos.receipt_kind", $"Unknown receipt kind '{q.Kind}'.");
            query = query.Where(r => r.Kind == kind);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Number).Skip((p - 1) * size).Take(size)
            .Select(r => new
            {
                r.Id, r.RegisterId, r.SessionId, r.Number, r.Kind, r.ReferencedReceiptId, r.CreatedAt, r.TotalGrossMinor, r.CreatedBy, r.CustomerId,
                Methods = db.Set<ReceiptPayment>().Where(x => x.ReceiptId == r.Id).Select(x => x.Method).ToList(),
                Reprints = db.Set<ReceiptReprint>().Count(x => x.ReceiptId == r.Id),
            })
            .ToListAsync(ct);

        var people = await users.GetAsync(rows.Select(r => r.CreatedBy).Distinct().ToList(), ct);
        var customerIds = rows.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var items = rows.Select(r => new ReceiptSummaryDto(r.Id, r.RegisterId, r.SessionId, r.Number, r.Kind.ToString(), r.ReferencedReceiptId, r.CreatedAt,
            r.TotalGrossMinor, r.CreatedBy, people.GetValueOrDefault(r.CreatedBy)?.DisplayName, r.CustomerId,
            r.CustomerId is { } c ? customers.GetValueOrDefault(c) : null, r.Methods.Order().Select(m => m.ToString()).ToList(), r.Reprints)).ToList();
        return new PagedResult<ReceiptSummaryDto>(items, p, size, total);
    }

    /// <summary>
    /// Records a reprint (copy) of an issued receipt and returns it with its copy number. The receipt itself is never changed;
    /// concurrent reprints are serialized per receipt and numbered without duplicates (unique index).
    /// </summary>
    public async Task<Result<ReprintDto>> ReprintAsync(Guid receiptId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var receipt = await LoadAsync(receiptId, ct);
        if (receipt is null)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"reprint:" + receiptId}, 0))", ct);
        var previous = await db.Set<ReceiptReprint>().CountAsync(r => r.ReceiptId == receiptId, ct);
        var reprint = ReceiptReprint.Record(receipt, previous, actor.UserId, clock.UtcNow);
        if (!reprint.IsSuccess)
            return reprint.Error!;
        db.Add(reprint.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReprintDto(reprint.Value.Sequence, reprint.Value.PrintedAt, reprint.Value.PrintedBy, await ToDtoAsync(receipt, ct));
    }

    /// <summary>Scanned code: 15 digits = IMEI of an own in-stock device; otherwise an EAN/GTIN from the catalog.</summary>
    public async Task<Result<LookupDto>> LookupAsync(string? code, Guid? registerId, CancellationToken ct)
    {
        var trimmed = code?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 20)
            return Error.Validation("pos.code", "Scan or enter an EAN or IMEI.");

        Guid? warehouseId = null;
        if (registerId is { } id)
        {
            warehouseId = await db.Set<Register>().AsNoTracking().Where(r => r.Id == id && r.OrganizationId == OrganizationId)
                .Select(r => (Guid?)r.WarehouseId).FirstOrDefaultAsync(ct);
            if (warehouseId is null)
                return Error.NotFound("pos.register_not_found", "Register not found.");
        }

        if (trimmed.Length == 15 && trimmed.All(char.IsAsciiDigit) && await inventory.FindOwnUnitByImeiAsync(trimmed, ct) is { } unit)
        {
            var device = (await catalog.GetAsync([unit.ProductId], ct))[unit.ProductId];
            var treatment = unit.TaxScheme == TaxScheme.Margin25a ? TaxTreatment.Margin25a : TaxTreatment.Standard19;
            var unitPrice = (await prices.ResolveAsync(unit.WarehouseId, [unit.ProductId], ct))[unit.ProductId].Price;
            return new LookupDto("Unit", unit.ProductId, device.Brand, device.Name, device.Ean, unit.Imei, unit.UnitId, treatment.ToString(),
                unit.WarehouseId, 1, 0, warehouseId is null || warehouseId == unit.WarehouseId ? 1 : 0, false, unitPrice.GrossMinor, unitPrice.Source.ToString());
        }

        if (await catalog.FindByEanAsync(trimmed, ct) is { } product)
        {
            StockSnapshot? level = null;
            var requiresImei = false;
            if (warehouseId is { } w)
            {
                level = (await inventory.GetLevelsAsync(w, [product.Id], ct)).GetValueOrDefault(product.Id) ?? new StockSnapshot(w, product.Id, 0, 0);
                requiresImei = await inventory.HasSerializedStockAsync(w, product.Id, ct);
            }
            var price = (await prices.ResolveAsync(warehouseId ?? Guid.Empty, [product.Id], ct))[product.Id].Price;
            return new LookupDto("Product", product.Id, product.Brand, product.Name, product.Ean, null, null, TaxTreatment.Standard19.ToString(),
                warehouseId, level?.OnHand, level?.Reserved, level is null ? null : level.OnHand - level.Reserved, requiresImei, price.GrossMinor,
                price.Source.ToString());
        }

        return Error.NotFound("pos.code_not_found", "No product with this EAN and no device with this IMEI in your stock.");
    }

    // ---- internals ------------------------------------------------------------------------------------------------------

    private CashierRights Rights() => new(actor.Has(Permissions.PosPriceOverride), actor.Has(Permissions.PosDiscount),
        actor.MaxDiscountPercent);

    /// <summary>Serializes storno/returns of the same original receipt (the receipt rows themselves can never be locked or updated).</summary>
    private Task LockOriginalAsync(Guid receiptId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({receiptId.ToString()}, 0))", ct);

    private async Task<Result<(Register Register, long Number, Guid SessionId)>> IssuingRegisterAsync(Guid registerId, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (actor.CheckRegister(register.Id) is { } otherRegister)
            return otherRegister;
        if (await PosSql.AllocateReceiptNumberAsync(db, register.Id, ct) is not { } number)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        // Refunds may pay out cash: exclusive lock so the drawer check below cannot race with withdrawals.
        if (await PosSql.LockOpenSessionAsync(db, register.Id, exclusive: true, ct) is not { } sessionId)
            return Error.Conflict("pos.no_open_session", "Open a cash session on this register first.");
        return (register, number, sessionId);
    }

    private async Task<Result<ReceiptDto>> CompleteRefundAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Receipt refund, Receipt original, Register register, CancellationToken ct)
    {
        var cashOut = -refund.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountMinor);
        if (cashOut > 0)
        {
            var session = await db.Set<CashSession>().AsNoTracking().FirstAsync(s => s.Id == refund.SessionId, ct);
            var expected = await PosSql.ExpectedCashAsync(db, session, ct);
            if (cashOut > expected)
                return Error.Conflict("pos.insufficient_cash",
                        $"Only {expected / 100m:0.00} € should be in the drawer; {cashOut / 100m:0.00} € cannot be paid out in cash.")
                    .WithDetails(new Dictionary<string, object?> { ["expectedCashMinor"] = expected });
        }

        var reference = $"pos:{register.Id}:{refund.Number}";
        foreach (var line in refund.Lines)
        {
            var restocked = line.StockUnitId is { } unitId
                ? await inventory.StageReturnUnitAsync(unitId, reference, ct)
                : await inventory.StageReturnAsync(original.WarehouseId, line.ProductId, -line.Quantity, reference, ct);
            if (!restocked.IsSuccess)
                return restocked.Error!;
        }

        var sealedResult = await SealAsync(refund, register.Name, original.Number, ct);
        if (!sealedResult.IsSuccess)
            return sealedResult.Error!;

        db.Add(refund);
        try
        {
            await db.SaveChangesAsync(ct);
            var use = refund.Kind == ReceiptKind.Storno ? ApprovalUse.Storno : ApprovalUse.Return;
            if (await approvals.ConsumeAsync(actor, use, refund.Id, null, null, ct) is { } approvalError)
                return approvalError;
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_stock_units_imei_in_stock"))
        {
            return Error.Conflict("unit.imei_in_stock", "The returned device is already in stock again (here or at another organization). Check its origin.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_receipts_one_storno"))
        {
            return Error.Conflict("pos.already_cancelled", "This receipt has already been cancelled.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ix_receipts_organization_id_idempotency_key"))
        {
            return Error.Conflict("pos.duplicate", "This return was already recorded. Reload the receipt.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("pos.stock_changed", "Stock changed concurrently. Try again.");
        }

        return await ToDtoAsync(refund, ct);
    }

    /// <summary>Fiscal signature (or the TSE outage marker, see <see cref="FiscalSealer"/>) and the printable document.</summary>
    private async Task<Result> SealAsync(Receipt receipt, string registerName, long? referencedNumber, CancellationToken ct)
    {
        if (await approvals.LockAsync(actor, ct) is { } approvalUnusable)
            return approvalUnusable; // checked before the TSE signs
        var signed = await fiscal.SignReceiptAsync(receipt, ct);
        if (!signed.IsSuccess)
            return signed.Error!;

        var seller = await organizations.GetLegalDetailsAsync(OrganizationId, ct)
                     ?? throw new InvalidOperationException("Organization of the current tenant not found.");
        var document = ReceiptDocument.For(receipt, new SellerInfo(seller.Name, seller.LegalName, seller.Street, seller.PostalCode, seller.City,
            seller.CountryCode, seller.VatId), registerName, referencedNumber, signed.Value);
        // A manager approval is printed with the receipt (who approved).
        if (actor.Approval is { ApproverName: { } approver })
            document = document with { Notes = [.. document.Notes, $"Freigabe: {approver}"] };
        return receipt.Seal(signed.Value, document);
    }

    private Task<Receipt?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Set<Receipt>().AsNoTracking().Include(r => r.Lines).Include(r => r.Payments).AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id && r.OrganizationId == OrganizationId, ct);

    private async Task<Guid?> StornoOfAsync(Guid receiptId, CancellationToken ct) =>
        await db.Set<Receipt>().AsNoTracking().Where(r => r.ReferencedReceiptId == receiptId && r.Kind == ReceiptKind.Storno)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);

    private async Task<Dictionary<int, int>> ReturnedQuantitiesAsync(Guid receiptId, CancellationToken ct) =>
        await (from l in db.Set<ReceiptLine>()
               join r in db.Set<Receipt>() on l.ReceiptId equals r.Id
               where r.ReferencedReceiptId == receiptId && r.Kind == ReceiptKind.Return && l.OriginalLineNumber != null
               group l by l.OriginalLineNumber!.Value into g
               select new { Line = g.Key, Quantity = -g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Line, x => x.Quantity, ct);

    private async Task<Result<SaleResult>?> ReplayAsync(string key, string requestHash, CancellationToken ct)
    {
        var existing = await db.Set<Receipt>().AsNoTracking().Include(r => r.Lines).Include(r => r.Payments).AsSplitQuery()
            .FirstOrDefaultAsync(r => r.OrganizationId == OrganizationId && r.IdempotencyKey == key, ct);
        if (existing is null)
            return null;
        if (existing.RequestHash != requestHash)
            return Error.Validation("pos.idempotency_key_reused",
                    "This Idempotency-Key was already used for a different request. Generate a new key for a new sale.")
                .WithDetails(new Dictionary<string, object?> { ["receiptId"] = existing.Id });
        return new SaleResult(await ToDtoAsync(existing, ct), Replayed: true);
    }

    /// <summary>Fingerprint of everything that defines the sale (line order matters: it is the receipt's line order).</summary>
    internal static string HashRequest(CreateSaleRequest request)
    {
        // Notes and the customer are appended only when present (length-prefixed), so fingerprints of requests without them
        // stay identical to those stored before these fields existed.
        var canonical = $"sale#{request.RegisterId:N}#"
                        + string.Join("|", request.Lines.Select(l =>
                            $"{l.ProductId:N}:{l.Imei?.Trim()}:{l.Quantity}:{l.UnitPriceMinor}:{l.DiscountMinor ?? 0}"
                            + (string.IsNullOrWhiteSpace(l.Note) ? "" : $":n{l.Note.Trim().Length}:{l.Note.Trim()}")))
                        + "#" + string.Join("|", request.Payments.OrderBy(p => p.Method).ThenBy(p => p.AmountMinor).Select(p => $"{p.Method}:{p.AmountMinor}"))
                        + (request.CustomerId is { } customer ? $"#c:{customer:N}" : "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string HashReturn(Guid receiptId, ReturnRequest request)
    {
        var canonical = $"return#{receiptId:N}#{request.RegisterId:N}#{request.RefundMethod}#"
                        + string.Join("|", request.Lines.OrderBy(l => l.LineNumber).Select(l => $"{l.LineNumber}:{l.Quantity}"))
                        + "#" + request.Reason.Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<ReceiptDto> ToDtoAsync(Receipt r, CancellationToken ct)
    {
        var registerName = await db.Set<Register>().AsNoTracking().Where(x => x.Id == r.RegisterId).Select(x => x.Name).FirstAsync(ct);
        long? referencedNumber = r.ReferencedReceiptId is { } referenced
            ? await db.Set<Receipt>().AsNoTracking().Where(x => x.Id == referenced).Select(x => (long?)x.Number).FirstOrDefaultAsync(ct)
            : null;
        var cancelledBy = r.Kind == ReceiptKind.Sale ? await StornoOfAsync(r.Id, ct) : null;
        var returned = cancelledBy is not null
            ? r.Lines.ToDictionary(l => l.LineNumber, l => l.Quantity)
            : r.Kind == ReceiptKind.Sale ? await ReturnedQuantitiesAsync(r.Id, ct) : new Dictionary<int, int>();

        var lines = r.Lines.OrderBy(l => l.LineNumber).Select(l => new ReceiptLineDto(l.LineNumber, l.ProductId, l.StockUnitId, l.Imei, l.Description,
            l.Quantity, l.UnitPriceMinor, l.DiscountMinor, l.LineGrossMinor, l.TaxTreatment.ToString(), l.VatRateBasisPoints,
            // §25a VAT is not disclosed (it would reveal the purchase price and must not be shown to the customer).
            l.TaxTreatment == TaxTreatment.Margin25a ? null : l.VatMinor,
            l.OriginalLineNumber, returned.GetValueOrDefault(l.LineNumber), l.Note)).ToList();

        var reprints = await db.Set<ReceiptReprint>().AsNoTracking().Where(x => x.ReceiptId == r.Id)
            .GroupBy(x => x.ReceiptId).Select(g => new { Count = g.Count(), Last = g.Max(x => x.PrintedAt) }).FirstOrDefaultAsync(ct);

        return new ReceiptDto(r.Id, r.RegisterId, registerName, r.SessionId, r.Number, r.Kind.ToString(), r.ReferencedReceiptId, referencedNumber, cancelledBy,
            r.CreatedAt, r.CreatedBy, (await users.GetAsync([r.CreatedBy], ct)).GetValueOrDefault(r.CreatedBy)?.DisplayName, r.Currency, r.TotalGrossMinor, r.ChangeMinor, r.Reason, lines,
            r.Payments.Select(p => new ReceiptPaymentDto(p.Method.ToString(), p.AmountMinor, p.TenderedMinor)).ToList(),
            r.Document!.Vat, r.Fiscal, r.Document, r.CustomerId, r.Document.Customer, reprints?.Count ?? 0, reprints?.Last,
            (await approvals.ForReceiptsAsync([r.Id], ct)).GetValueOrDefault(r.Id), r.RoundingMinor);
    }
}
