using FluentValidation;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Ankauf.Application;

public sealed record ReceiveIntakeRequest(Guid QuoteId, string Imei, SellerDetails Seller);

public sealed record InspectIntakeRequest(DeviceCondition Condition, DeviceDefect[]? Defects, int? BatteryHealthPercent, string? Notes);

public sealed record CompleteIntakeRequest(decimal PaidAmount, PaymentMethod PaymentMethod, Guid? WarehouseId);

public sealed record RejectIntakeRequest(string Reason);

public sealed record BelegFile(string FileName, byte[] Content);

public sealed record IntakeDto(
    Guid Id,
    Guid QuoteId,
    string Brand,
    string Model,
    string? Storage,
    string Imei,
    string Status,
    string SellerName,
    DateTimeOffset ReceivedAt,
    long QuotedOfferMinor,
    string? InspectedCondition,
    IReadOnlyList<string> InspectedDefects,
    int? InspectedBatteryHealthPercent,
    string? InspectionNotes,
    long? InspectedOfferMinor,
    long? InspectedMaxConsumerPriceMinor,
    long? PaidMinor,
    string? PaymentMethod,
    Guid? StockUnitId,
    DateTimeOffset? CompletedAt,
    string? RejectionReason);

internal sealed class ReceiveIntakeValidator : AbstractValidator<ReceiveIntakeRequest>
{
    public ReceiveIntakeValidator()
    {
        RuleFor(x => x.QuoteId).NotEmpty();
        RuleFor(x => x.Imei).NotEmpty().Must(i => Imei.TryNormalize(i, out _)).WithMessage("IMEI must be 15 digits with a valid check digit.");
        RuleFor(x => x.Seller).NotNull().SetValidator(new SellerValidator());
    }
}

internal sealed class SellerValidator : AbstractValidator<SellerDetails>
{
    public SellerValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Street).NotEmpty().MaximumLength(200);
        RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(10);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.IdDocumentType).IsInEnum();
        RuleFor(x => x.IdDocumentNumber).NotEmpty().MaximumLength(30);
    }
}

internal sealed class IntakeService(
    HandyNetDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IProductCatalog catalog,
    IInventory inventory,
    IOrganizationDirectory organizations,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<IntakeDto>> ReceiveAsync(ReceiveIntakeRequest request, CancellationToken ct)
    {
        var quote = await db.Set<Quote>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == request.QuoteId && q.OrganizationId == OrganizationId, ct);
        if (quote is null)
            return Error.NotFound("ankauf.quote_not_found", "Quote not found.");
        if (await db.Set<Intake>().AnyAsync(i => i.QuoteId == quote.Id && i.Status != IntakeStatus.Rejected, ct))
            return Error.Conflict("ankauf.quote_used", "This quote is already used for an intake.");
        if (Imei.TryNormalize(request.Imei, out var imei) && await inventory.IsImeiInStockAnywhereAsync(imei, ct))
            return Error.Conflict("unit.imei_in_stock", "This IMEI is already in stock. Do not buy the device; check its origin.");

        var intake = Intake.Receive(OrganizationId, quote, request.Imei, request.Seller, currentUser.UserId, clock.UtcNow);
        if (!intake.IsSuccess)
            return intake.Error!;
        db.Add(intake.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("ankauf.quote_used", "This quote is already used for an intake.");
        }
        return ToDto(intake.Value, quote);
    }

    /// <summary>Recalculates with the quote's price version and policy — the price the dealer was quoted, adjusted for the findings.</summary>
    public async Task<Result<IntakeDto>> InspectAsync(Guid id, InspectIntakeRequest request, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");

        var price = await db.Set<PriceVersion>().AsNoTracking().FirstAsync(v => v.Id == quote!.PriceVersionId, ct);
        var policy = await db.Set<ConditionPolicy>().AsNoTracking().FirstAsync(p => p.Id == quote!.ConditionPolicyId, ct);
        var findings = new QuoteInput(request.Condition, request.Defects ?? [], request.BatteryHealthPercent, null);
        var calculation = BuybackCalculator.Calculate(price.PriceMinor, policy.Settings, price.ModelKey.Split('|')[0], price.ModelKey, findings);

        var inspected = intake.Inspect(findings, calculation, request.Notes, clock.UtcNow);
        if (!inspected.IsSuccess)
            return inspected.Error!;
        return await SaveAsync(intake, quote!, ct);
    }

    /// <summary>Pays the seller and stocks the device (catalog product, IMEI unit, +1 stock, movement) in one transaction.</summary>
    public async Task<Result<IntakeDto>> CompleteAsync(Guid id, CompleteIntakeRequest request, CancellationToken ct)
    {
        if (!Money.TryFromMajor(request.PaidAmount, Money.Eur, out var paid))
            return Error.Validation("ankauf.paid_amount", "The amount may have at most two decimal places.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (intake, quote) = await LoadAsync(id, ct);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        if (intake.Status != IntakeStatus.Inspected)
            return Error.Conflict("ankauf.not_inspected", "Inspect the device before paying the seller.");

        var warehouseId = request.WarehouseId ?? await inventory.GetOrStageDefaultWarehouseAsync(ct);
        var product = await catalog.FindOrStageDeviceAsync(quote!.Brand, quote.Model, quote.Storage, OrganizationId, ct);
        if (!product.IsSuccess)
            return product.Error!;

        // Bought from a private person without VAT → §25a margin scheme on resale.
        var unit = await inventory.StageReceiveUnitAsync(warehouseId, product.Value.Id, intake.Imei, paid.MinorUnits, TaxScheme.Margin25a,
            $"ankauf:{intake.Id}", ct);
        if (!unit.IsSuccess)
            return unit.Error!;

        var completed = intake.Complete(paid.MinorUnits, request.PaymentMethod, unit.Value, clock.UtcNow);
        if (!completed.IsSuccess)
            return completed.Error!;

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("unit.imei_in_stock", "This IMEI was stocked concurrently. The purchase was not recorded.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The intake was changed concurrently. Reload and try again.");
        }
        return ToDto(intake, quote);
    }

    public async Task<Result<IntakeDto>> RejectAsync(Guid id, string reason, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        var rejected = intake.Reject(reason, clock.UtcNow);
        if (!rejected.IsSuccess)
            return rejected.Error!;
        return await SaveAsync(intake, quote!, ct);
    }

    public async Task<IReadOnlyList<IntakeDto>> ListAsync(CancellationToken ct)
    {
        var rows = await (
                from i in db.Set<Intake>().AsNoTracking()
                join q in db.Set<Quote>().AsNoTracking() on i.QuoteId equals q.Id
                where i.OrganizationId == OrganizationId
                orderby i.ReceivedAt descending
                select new { i, q })
            .Take(100)
            .ToListAsync(ct);
        return rows.Select(r => ToDto(r.i, r.q)).ToList();
    }

    public async Task<Result<IntakeDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct);
        return intake is null ? Error.NotFound("ankauf.intake_not_found", "Intake not found.") : ToDto(intake, quote!);
    }

    /// <summary>Purchase receipt (PDF) for the seller of a completed intake.</summary>
    public async Task<Result<BelegFile>> GetBelegAsync(Guid id, CancellationToken ct)
    {
        var (intake, quote) = await LoadAsync(id, ct, tracking: false);
        if (intake is null)
            return Error.NotFound("ankauf.intake_not_found", "Intake not found.");
        if (intake.Status != IntakeStatus.Completed)
            return Error.Conflict("ankauf.not_completed", "A purchase receipt is only available for a completed intake.");

        var dealer = await organizations.GetAddressAsync(OrganizationId, ct);
        if (dealer is null)
            return Error.NotFound("organization.not_found", "Organization not found.");

        var content = Ankaufbeleg.Build(intake, quote!,
            new BelegDealer(dealer.Name, dealer.LegalName, dealer.Street, dealer.PostalCode, dealer.City, dealer.CountryCode, dealer.VatId),
            Ankaufbeleg.GermanTimeZone);
        if (!content.IsSuccess)
            return content.Error!;
        return new BelegFile($"{content.Value.DocumentNumber}.pdf", AnkaufbelegPdf.Render(content.Value));
    }

    private async Task<(Intake? Intake, Quote? Quote)> LoadAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var intakes = tracking ? db.Set<Intake>() : db.Set<Intake>().AsNoTracking();
        var intake = await intakes.FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (intake is null)
            return (null, null);
        var quote = await db.Set<Quote>().AsNoTracking().FirstAsync(q => q.Id == intake.QuoteId, ct);
        return (intake, quote);
    }

    private async Task<Result<IntakeDto>> SaveAsync(Intake intake, Quote quote, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The intake was changed concurrently. Reload and try again.");
        }
        return ToDto(intake, quote);
    }

    private static IntakeDto ToDto(Intake i, Quote q) => new(
        i.Id, i.QuoteId, q.Brand, q.Model, q.Storage, i.Imei, i.Status.ToString(), $"{i.Seller.FirstName} {i.Seller.LastName}", i.ReceivedAt,
        q.PlatformOfferMinor, i.InspectedCondition?.ToString(), i.InspectedDefects.Select(d => d.ToString()).ToList(), i.InspectedBatteryHealthPercent,
        i.InspectionNotes, i.InspectedOfferMinor, i.InspectedMaxConsumerPriceMinor, i.PaidMinor, i.PaymentMethod?.ToString(), i.StockUnitId,
        i.CompletedAt, i.RejectionReason);
}
