using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
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

/// <summary>A product line (ProductId, any quantity) or a serialized device (Imei, quantity 1). Prices are gross, in cents.</summary>
public sealed record SaleLineRequest(Guid? ProductId, string? Imei, int Quantity, long UnitPriceMinor, long? DiscountMinor);

public sealed record SalePaymentRequest(PaymentMethod Method, long AmountMinor);

public sealed record CreateSaleRequest(Guid RegisterId, IReadOnlyList<SaleLineRequest> Lines, IReadOnlyList<SalePaymentRequest> Payments);

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
    int ReturnedQuantity);

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
    string Currency,
    long TotalGrossMinor,
    long ChangeMinor,
    string? Reason,
    IReadOnlyList<ReceiptLineDto> Lines,
    IReadOnlyList<ReceiptPaymentDto> Payments,
    IReadOnlyList<DocumentVat> Vat,
    FiscalData? Fiscal,
    ReceiptDocument Document);

public sealed record ReceiptSummaryDto(Guid Id, Guid RegisterId, Guid SessionId, long Number, string Kind, Guid? ReferencedReceiptId,
    DateTimeOffset CreatedAt, long TotalGrossMinor);

public sealed record SaleResult(ReceiptDto Receipt, bool Replayed);

/// <summary>Kind = "Product" (EAN) or "Unit" (IMEI). Stock figures refer to the register's warehouse (null without registerId).</summary>
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
    int? Available);

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
        });
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

/// <summary>Resolves the configured fiscal signer; null when none is configured (sales are then refused).</summary>
internal sealed class FiscalSignerProvider(IOptions<PosOptions> options)
{
    public IFiscalSigner? Current => options.Value.FiscalSigner switch
    {
        FiscalSignerKind.Disabled => new DisabledFiscalSigner(),
        _ => null,
    };
}

internal sealed class SaleService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IInventory inventory,
    IProductCatalog catalog,
    IOrganizationDirectory organizations,
    FiscalSignerProvider signers,
    IClock clock,
    ILogger<SaleService> logger)
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
        if (signers.Current is not { } signer)
            return FiscalMissing();

        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RegisterId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");

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

        var inputs = staged.Select(s =>
        {
            var productId = s.Unit?.ProductId ?? s.Request.ProductId!.Value;
            var product = products[productId];
            var margin = s.Unit?.TaxScheme == TaxScheme.Margin25a;
            return new SaleLineInput(productId, s.Unit?.UnitId, s.Unit?.Imei, $"{product.Brand} {product.Name}", s.Request.Quantity,
                s.Request.UnitPriceMinor, s.Request.DiscountMinor ?? 0, margin ? TaxTreatment.Margin25a : TaxTreatment.Standard19,
                margin ? s.Unit!.PurchasePriceMinor : null);
        }).ToList();

        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, register.WarehouseId, number, currentUser.UserId, clock.UtcNow,
            idempotencyKey, requestHash);
        var receipt = Receipt.CreateSale(header, inputs, request.Payments.Select(p => new PaymentInput(p.Method, p.AmountMinor)).ToList());
        if (!receipt.IsSuccess)
            return receipt.Error!;

        var sealedResult = await SealAsync(signer, receipt.Value, register.Name, null, ct);
        if (!sealedResult.IsSuccess)
            return sealedResult.Error!;

        db.Add(receipt.Value);
        try
        {
            await db.SaveChangesAsync(ct);
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
        if (signers.Current is not { } signer)
            return FiscalMissing();

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

        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, original.WarehouseId, number, currentUser.UserId, clock.UtcNow);
        var storno = Receipt.CreateStorno(header, original, hasReturns, request.Reason);
        if (!storno.IsSuccess)
            return storno.Error!;

        return await CompleteRefundAsync(tx, signer, storno.Value, original, register, ct);
    }

    /// <summary>Returns selected quantities of a sale's lines with a new negative receipt; refunded by the given method.</summary>
    public async Task<Result<SaleResult>> ReturnAsync(Guid receiptId, ReturnRequest request, string idempotencyKey, CancellationToken ct)
    {
        var requestHash = HashReturn(receiptId, request);
        if (await ReplayAsync(idempotencyKey, requestHash, ct) is { } replay)
            return replay;
        if (signers.Current is not { } signer)
            return FiscalMissing();

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

        var header = new ReceiptHeader(OrganizationId, register.Id, sessionId, original.WarehouseId, number, currentUser.UserId, clock.UtcNow,
            idempotencyKey, requestHash);
        var returned = Receipt.CreateReturn(header, original, request.Lines.ToDictionary(l => l.LineNumber, l => l.Quantity), alreadyReturned,
            request.RefundMethod, request.Reason);
        if (!returned.IsSuccess)
            return returned.Error!;

        var completed = await CompleteRefundAsync(tx, signer, returned.Value, original, register, ct);
        return completed.IsSuccess ? new SaleResult(completed.Value, Replayed: false) : completed.Error!;
    }

    public async Task<Result<ReceiptDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var receipt = await LoadAsync(id, ct);
        return receipt is null ? Error.NotFound("pos.receipt_not_found", "Receipt not found.") : await ToDtoAsync(receipt, ct);
    }

    public async Task<PagedResult<ReceiptSummaryDto>> ListAsync(Guid? registerId, Guid? sessionId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Receipt>().AsNoTracking().Where(r => r.OrganizationId == OrganizationId);
        if (registerId is { } register)
            query = query.Where(r => r.RegisterId == register);
        if (sessionId is { } session)
            query = query.Where(r => r.SessionId == session);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Number).Skip((p - 1) * size).Take(size)
            .Select(r => new ReceiptSummaryDto(r.Id, r.RegisterId, r.SessionId, r.Number, r.Kind.ToString(), r.ReferencedReceiptId, r.CreatedAt, r.TotalGrossMinor))
            .ToListAsync(ct);
        return new PagedResult<ReceiptSummaryDto>(items, p, size, total);
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
            return new LookupDto("Unit", unit.ProductId, device.Brand, device.Name, device.Ean, unit.Imei, unit.UnitId, treatment.ToString(),
                unit.WarehouseId, 1, 0, warehouseId is null || warehouseId == unit.WarehouseId ? 1 : 0);
        }

        if (await catalog.FindByEanAsync(trimmed, ct) is { } product)
        {
            StockSnapshot? level = null;
            if (warehouseId is { } w)
                level = (await inventory.GetLevelsAsync(w, [product.Id], ct)).GetValueOrDefault(product.Id) ?? new StockSnapshot(w, product.Id, 0, 0);
            return new LookupDto("Product", product.Id, product.Brand, product.Name, product.Ean, null, null, TaxTreatment.Standard19.ToString(),
                warehouseId, level?.OnHand, level?.Reserved, level is null ? null : level.OnHand - level.Reserved);
        }

        return Error.NotFound("pos.code_not_found", "No product with this EAN and no device with this IMEI in your stock.");
    }

    // ---- internals ------------------------------------------------------------------------------------------------------

    private static Error FiscalMissing() =>
        Error.Conflict("pos.fiscal_signer_missing", "No fiscal signer (TSE) is configured. POS sales are not possible.");

    /// <summary>Serializes storno/returns of the same original receipt (the receipt rows themselves can never be locked or updated).</summary>
    private Task LockOriginalAsync(Guid receiptId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({receiptId.ToString()}, 0))", ct);

    private async Task<Result<(Register Register, long Number, Guid SessionId)>> IssuingRegisterAsync(Guid registerId, CancellationToken ct)
    {
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        if (await PosSql.AllocateReceiptNumberAsync(db, register.Id, ct) is not { } number)
            return Error.Conflict("pos.register_inactive", "The register is deactivated.");
        // Refunds may pay out cash: exclusive lock so the drawer check below cannot race with withdrawals.
        if (await PosSql.LockOpenSessionAsync(db, register.Id, exclusive: true, ct) is not { } sessionId)
            return Error.Conflict("pos.no_open_session", "Open a cash session on this register first.");
        return (register, number, sessionId);
    }

    private async Task<Result<ReceiptDto>> CompleteRefundAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, IFiscalSigner signer,
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

        var sealedResult = await SealAsync(signer, refund, register.Name, original.Number, ct);
        if (!sealedResult.IsSuccess)
            return sealedResult.Error!;

        db.Add(refund);
        try
        {
            await db.SaveChangesAsync(ct);
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

    private async Task<Result> SealAsync(IFiscalSigner signer, Receipt receipt, string registerName, long? referencedNumber, CancellationToken ct)
    {
        var started = await signer.StartTransactionAsync(new FiscalTransactionRequest(OrganizationId, receipt.RegisterId, receipt.Kind, receipt.Number), ct);
        if (!started.IsSuccess)
            return started.Error!;
        var summary = SessionCalculator.Summarize(0, [receipt], []);
        var fiscal = await signer.FinishTransactionAsync(started.Value,
            new FiscalReceiptData(receipt.Kind, receipt.Number, receipt.TotalGrossMinor, summary.ByTaxTreatment, summary.ByPaymentMethod), ct);
        if (!fiscal.IsSuccess)
            return fiscal.Error!;

        var seller = await organizations.GetLegalDetailsAsync(OrganizationId, ct)
                     ?? throw new InvalidOperationException("Organization of the current tenant not found.");
        var document = ReceiptDocument.For(receipt, new SellerInfo(seller.Name, seller.LegalName, seller.Street, seller.PostalCode, seller.City,
            seller.CountryCode, seller.VatId), registerName, referencedNumber, fiscal.Value);
        return receipt.Seal(fiscal.Value, document);
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
        var canonical = $"sale#{request.RegisterId:N}#"
                        + string.Join("|", request.Lines.Select(l =>
                            $"{l.ProductId:N}:{l.Imei?.Trim()}:{l.Quantity}:{l.UnitPriceMinor}:{l.DiscountMinor ?? 0}"))
                        + "#" + string.Join("|", request.Payments.OrderBy(p => p.Method).ThenBy(p => p.AmountMinor).Select(p => $"{p.Method}:{p.AmountMinor}"));
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
            l.OriginalLineNumber, returned.GetValueOrDefault(l.LineNumber))).ToList();

        return new ReceiptDto(r.Id, r.RegisterId, registerName, r.SessionId, r.Number, r.Kind.ToString(), r.ReferencedReceiptId, referencedNumber, cancelledBy,
            r.CreatedAt, r.CreatedBy, r.Currency, r.TotalGrossMinor, r.ChangeMinor, r.Reason, lines,
            r.Payments.Select(p => new ReceiptPaymentDto(p.Method.ToString(), p.AmountMinor, p.TenderedMinor)).ToList(),
            r.Document!.Vat, r.Fiscal, r.Document);
    }
}
