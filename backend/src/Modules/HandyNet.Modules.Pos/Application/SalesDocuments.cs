using FluentValidation;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Documents;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

// ---- requests ---------------------------------------------------------------------------------------------------------------

/// <summary>
/// A line: a device by IMEI (own in-stock unit; §25a follows the unit), a catalog product (ProductId) or free text (Description
/// only, no stock). Prices in cents in the document's price mode. VatRateBasisPoints: 1900, 700 or 0 (default 1900).
/// </summary>
public sealed record SalesLineRequest(
    Guid? ProductId,
    string? Imei,
    string? Description,
    string? Note,
    int Quantity,
    long UnitPriceMinor,
    long? DiscountMinor,
    int? VatRateBasisPoints);

public sealed record SalesCustomerRequest(string Name, string? Company, string? Street, string? PostalCode, string? City, string? CountryCode,
    string? VatId);

/// <summary>CustomerId: a POS customer (its data is copied); otherwise Customer as entered (quotations need one of the two).</summary>
public sealed record QuotationRequest(
    Guid? CustomerId,
    SalesCustomerRequest? Customer,
    DateOnly? ValidUntil,
    PriceMode? PriceMode,
    IReadOnlyList<SalesLineRequest> Lines,
    string? Notes);

public sealed record QuotationStatusRequest(QuotationStatus Status);

public sealed record SalesInvoiceRequest(
    Guid? CustomerId,
    SalesCustomerRequest? Customer,
    DateOnly? ServiceDate,
    int? PaymentTermDays,
    PriceMode? PriceMode,
    bool? ReverseCharge,
    Guid? WarehouseId,
    IReadOnlyList<SalesLineRequest> Lines,
    string? Notes);

/// <summary>Bank transfer, card (outside the till) or other. Cash received in the shop is collected at the till.</summary>
public sealed record InvoicePaymentRequest(InvoicePaymentMethod Method, long AmountMinor, DateOnly? PaidOn, string? Reference);

/// <summary>RegisterId: needed with a manager approval (X-Pos-Approval), which is bound to a register.</summary>
public sealed record CreditNoteRequest(string Reason, Guid? RegisterId);

/// <summary>Collect at the till: AmountMinor applied to the invoice (null = the open amount); payments as at a sale (cash with change, card exact).</summary>
public sealed record CollectInvoiceRequest(Guid RegisterId, long? AmountMinor, IReadOnlyList<SalePaymentRequest> Payments);

public sealed record SalesEmailRequest(string? Email);

public sealed record SalesQuery(
    string? Search,
    string? Status,
    Guid? CustomerId,
    DateOnly? From,
    DateOnly? To,
    int? Page,
    int? PageSize,
    string? Kind = null,
    string? PaymentState = null,
    bool? Overdue = null);

// ---- responses --------------------------------------------------------------------------------------------------------------

public sealed record QuotationDto(
    Guid Id,
    string Number,
    string Status,
    string StoredStatus,
    bool Editable,
    Guid? CustomerId,
    SalesCustomer Customer,
    DateOnly IssueDate,
    DateOnly ValidUntil,
    string PriceMode,
    IReadOnlyList<SalesLine> Lines,
    IReadOnlyList<SalesVat> Vat,
    long MarginGrossMinor,
    long TotalNetMinor,
    long TotalVatMinor,
    long TotalGrossMinor,
    string? Notes,
    Guid? ConvertedInvoiceId,
    string? ConvertedInvoiceNumber,
    DateTimeOffset? SentAt,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    string? CreatedByName,
    DateTimeOffset UpdatedAt,
    string PdfUrl);

public sealed record QuotationSummaryDto(Guid Id, string Number, string Status, string CustomerName, string? CustomerCompany, DateOnly IssueDate,
    DateOnly ValidUntil, long TotalGrossMinor, Guid? ConvertedInvoiceId);

public sealed record InvoicePaymentDto(Guid Id, string Method, long AmountMinor, DateOnly PaidOn, Guid? ReceiptId, long? ReceiptNumber, string? Reference,
    DateTimeOffset CreatedAt, Guid CreatedBy, string? CreatedByName);

public sealed record SalesInvoiceDto(
    Guid Id,
    string Kind,
    string Status,
    string? Number,
    Guid? CustomerId,
    SalesCustomer? Customer,
    DateOnly? InvoiceDate,
    DateOnly? ServiceDate,
    int PaymentTermDays,
    DateOnly? DueDate,
    string PriceMode,
    bool ReverseCharge,
    Guid? WarehouseId,
    Guid? QuotationId,
    string? QuotationNumber,
    Guid? OriginalInvoiceId,
    string? OriginalNumber,
    Guid? CreditNoteId,
    string? CreditNoteNumber,
    string? Reason,
    string? Notes,
    IReadOnlyList<SalesLine> Lines,
    IReadOnlyList<SalesVat> Vat,
    long MarginGrossMinor,
    long TotalNetMinor,
    long TotalVatMinor,
    long TotalGrossMinor,
    long PaidMinor,
    long OpenMinor,
    string? PaymentState,
    bool Overdue,
    bool Cancelled,
    IReadOnlyList<InvoicePaymentDto> Payments,
    DateTimeOffset? IssuedAt,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    string? CreatedByName,
    string PdfUrl,
    IReadOnlyList<string> Warnings);

/// <summary>Origin: "Sale" (issued here) or "Receipt" (invoice for a POS sale, read-only, paid at the till).</summary>
public sealed record InvoiceListItemDto(
    Guid Id,
    string Origin,
    string Kind,
    string Status,
    string? Number,
    string? CustomerName,
    string? CustomerCompany,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    long TotalGrossMinor,
    long PaidMinor,
    string? PaymentState,
    bool Overdue,
    bool Cancelled,
    Guid? ReceiptId,
    DateTimeOffset SortAt);

public sealed record SalesInvoiceSummaryDto(int OverdueCount, long OverdueMinor, int OpenCount, long OpenMinor, int DraftCount);

public sealed record SalesInvoiceResult(SalesInvoiceDto Invoice, bool Existing);

public sealed record SalesEmailDto(Guid Id, string RecipientMasked, DateTimeOffset RequestedAt);

public sealed record SalesWarehouseDto(Guid Id, string Code, string Name, bool IsDefault);

public sealed record CollectInvoiceResult(ReceiptDto Receipt, Guid InvoiceId, bool Replayed);

// ---- validation -------------------------------------------------------------------------------------------------------------

internal sealed class SalesLineRequestValidator : AbstractValidator<SalesLineRequest>
{
    public SalesLineRequestValidator()
    {
        RuleFor(l => l).Must(l => l.ProductId is not null || !string.IsNullOrWhiteSpace(l.Imei) || !string.IsNullOrWhiteSpace(l.Description))
            .WithName("Line").WithMessage("Each line needs a product, an IMEI or a description.");
        RuleFor(l => l.Imei).Must(i => i is null || (i.Trim().Length == 15 && i.Trim().All(char.IsAsciiDigit))).WithMessage("IMEI must have 15 digits.");
        RuleFor(l => l.Quantity).InclusiveBetween(1, SalesCalculator.MaxQuantity);
        RuleFor(l => l.Quantity).Equal(1).When(l => !string.IsNullOrWhiteSpace(l.Imei)).WithMessage("A device (IMEI) is sold with quantity 1.");
        RuleFor(l => l.UnitPriceMinor).InclusiveBetween(0, SalesCalculator.MaxAmountMinor);
        RuleFor(l => l.DiscountMinor).GreaterThanOrEqualTo(0).When(l => l.DiscountMinor is not null);
        RuleFor(l => l.Description).MaximumLength(SalesCalculator.MaxDescriptionLength);
        RuleFor(l => l.Note).MaximumLength(SalesCalculator.MaxNoteLength);
        RuleFor(l => l.VatRateBasisPoints).Must(r => r is null || SalesCalculator.Rates.Contains(r.Value)).WithMessage("The VAT rate must be 1900, 700 or 0.");
    }
}

internal sealed class SalesCustomerRequestValidator : AbstractValidator<SalesCustomerRequest>
{
    public SalesCustomerRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Customer.MaxNameLength);
        RuleFor(x => x.Company).MaximumLength(Customer.MaxNameLength);
        RuleFor(x => x.Street).MaximumLength(Customer.MaxStreetLength);
        RuleFor(x => x.PostalCode).MaximumLength(Customer.MaxPostalCodeLength);
        RuleFor(x => x.City).MaximumLength(Customer.MaxCityLength);
        RuleFor(x => x.CountryCode).Length(2).When(x => !string.IsNullOrWhiteSpace(x.CountryCode));
        RuleFor(x => x.VatId).MaximumLength(Customer.MaxVatIdLength + 5);
    }
}

internal sealed class QuotationRequestValidator : AbstractValidator<QuotationRequest>
{
    public QuotationRequestValidator()
    {
        RuleFor(x => x).Must(x => x.CustomerId is not null || x.Customer is not null).WithName("Customer")
            .WithMessage("Choose a customer or enter one.");
        RuleFor(x => x.Customer!).SetValidator(new SalesCustomerRequestValidator()).When(x => x.Customer is not null);
        RuleFor(x => x.PriceMode).IsInEnum().When(x => x.PriceMode is not null);
        RuleFor(x => x.Lines).NotNull().Must(l => l is null || l.Count <= SalesCalculator.MaxLines).WithMessage($"At most {SalesCalculator.MaxLines} lines.");
        RuleForEach(x => x.Lines).SetValidator(new SalesLineRequestValidator());
        RuleFor(x => x.Notes).MaximumLength(Quotation.MaxNotesLength);
    }
}

internal sealed class QuotationStatusRequestValidator : AbstractValidator<QuotationStatusRequest>
{
    public QuotationStatusRequestValidator() => RuleFor(x => x.Status).IsInEnum();
}

internal sealed class SalesInvoiceRequestValidator : AbstractValidator<SalesInvoiceRequest>
{
    public SalesInvoiceRequestValidator()
    {
        RuleFor(x => x.Customer!).SetValidator(new SalesCustomerRequestValidator()).When(x => x.Customer is not null);
        RuleFor(x => x.PriceMode).IsInEnum().When(x => x.PriceMode is not null);
        RuleFor(x => x.PaymentTermDays).InclusiveBetween(0, SalesInvoice.MaxPaymentTermDays).When(x => x.PaymentTermDays is not null);
        RuleFor(x => x.Lines).NotNull().Must(l => l is null || l.Count <= SalesCalculator.MaxLines).WithMessage($"At most {SalesCalculator.MaxLines} lines.");
        RuleForEach(x => x.Lines).SetValidator(new SalesLineRequestValidator());
        RuleFor(x => x.Notes).MaximumLength(SalesInvoice.MaxNotesLength);
    }
}

internal sealed class InvoicePaymentRequestValidator : AbstractValidator<InvoicePaymentRequest>
{
    public InvoicePaymentRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.AmountMinor).InclusiveBetween(1, SalesCalculator.MaxAmountMinor);
        RuleFor(x => x.Reference).MaximumLength(SalesInvoicePayment.MaxReferenceLength);
    }
}

internal sealed class CreditNoteRequestValidator : AbstractValidator<CreditNoteRequest>
{
    public CreditNoteRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(SalesInvoice.MaxReasonLength);
}

internal sealed class CollectInvoiceRequestValidator : AbstractValidator<CollectInvoiceRequest>
{
    public CollectInvoiceRequestValidator()
    {
        RuleFor(x => x.RegisterId).NotEmpty();
        RuleFor(x => x.AmountMinor).InclusiveBetween(1, SalesCalculator.MaxAmountMinor).When(x => x.AmountMinor is not null);
        RuleFor(x => x.Payments).NotEmpty().Must(p => p is null || p.Count <= 10).WithMessage("At most 10 payments.");
        RuleForEach(x => x.Payments).ChildRules(p =>
        {
            p.RuleFor(x => x.Method).IsInEnum();
            p.RuleFor(x => x.AmountMinor).InclusiveBetween(1, PosLimits.MaxAmountMinor);
        });
    }
}

internal sealed class SalesEmailRequestValidator : AbstractValidator<SalesEmailRequest>
{
    public SalesEmailRequestValidator() => RuleFor(x => x.Email).MaximumLength(ReceiptEmail.MaxEmailLength);
}

// ---- shared helpers ---------------------------------------------------------------------------------------------------------

internal static class BerlinTime
{
    private static readonly TimeZoneInfo Zone = Find();

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    public static DateOnly DateOf(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);

    private static TimeZoneInfo Find()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

/// <summary>Number allocation (raw SQL, row-locked until commit; a rollback gives the number back).</summary>
internal static class DocumentNumbers
{
    /// <summary>
    /// Next number of the organization's single invoice series (RE-YYYY-nnnnnn), shared by invoices for POS sales, sales invoices
    /// and credit notes. Gapless, enforced by triggers (counter +1 only; a deferred check fails a commit that took a number without
    /// writing its invoice).
    /// </summary>
    public static async Task<long> InvoiceAsync(HandyNetDbContext db, Guid organizationId, int year, CancellationToken ct) =>
        (await db.Database.SqlQuery<long>($"""
            INSERT INTO pos.invoice_sequences (organization_id, year, next_number) VALUES ({organizationId}, {year}, 2)
            ON CONFLICT (organization_id, year) DO UPDATE SET next_number = pos.invoice_sequences.next_number + 1
            RETURNING next_number - 1 AS "Value"
            """).ToListAsync(ct)).Single();

    /// <summary>Next quotation number (AN-YYYY-nnnn): sequential per organization and year, taken in the creating transaction.</summary>
    public static async Task<long> QuotationAsync(HandyNetDbContext db, Guid organizationId, int year, CancellationToken ct) =>
        (await db.Database.SqlQuery<long>($"""
            INSERT INTO pos.quotation_sequences (organization_id, year, next_number) VALUES ({organizationId}, {year}, 2)
            ON CONFLICT (organization_id, year) DO UPDATE SET next_number = pos.quotation_sequences.next_number + 1
            RETURNING next_number - 1 AS "Value"
            """).ToListAsync(ct)).Single();
}

/// <summary>Turns request lines and the chosen customer into domain input (catalog names, own IMEI units, §25a from the unit).</summary>
internal sealed class SalesInputResolver(HandyNetDbContext db, ITenantContext tenant, IInventory inventory, IProductCatalog catalog)
{
    public async Task<Result<List<SalesLineInput>>> LinesAsync(IReadOnlyList<SalesLineRequest> requests, CancellationToken ct)
    {
        var productIds = requests.Where(r => r.ProductId is not null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var products = await catalog.GetAsync(productIds, ct);
        var result = new List<SalesLineInput>();
        var index = 0;
        foreach (var r in requests)
        {
            index++;
            var vat = r.VatRateBasisPoints ?? VatCalculator.StandardRateBasisPoints;
            if (!string.IsNullOrWhiteSpace(r.Imei))
            {
                var unit = await inventory.FindOwnUnitByImeiAsync(r.Imei.Trim(), ct);
                if (unit is null)
                    return Error.NotFound("pos.unit_not_found", $"Line {index}: no device with IMEI {r.Imei.Trim()} in your stock.");
                if (r.ProductId is { } expected && expected != unit.ProductId)
                    return Error.Validation("pos.imei_product_mismatch", $"Line {index}: the device {r.Imei.Trim()} is not of the selected product.");
                var device = products.GetValueOrDefault(unit.ProductId) ?? (await catalog.GetAsync([unit.ProductId], ct)).GetValueOrDefault(unit.ProductId);
                var margin = unit.TaxScheme == Inventory.Domain.TaxScheme.Margin25a;
                result.Add(new SalesLineInput(unit.ProductId, unit.UnitId, unit.Imei, Describe(r.Description, device), r.Note, 1, r.UnitPriceMinor,
                    r.DiscountMinor ?? 0, margin ? VatCalculator.StandardRateBasisPoints : vat, margin));
            }
            else if (r.ProductId is { } productId)
            {
                if (!products.TryGetValue(productId, out var product))
                    return Error.NotFound("pos.product_not_found", $"Line {index}: product not found.");
                result.Add(new SalesLineInput(productId, null, null, Describe(r.Description, product), r.Note, r.Quantity, r.UnitPriceMinor,
                    r.DiscountMinor ?? 0, vat, false));
            }
            else
            {
                result.Add(new SalesLineInput(null, null, null, r.Description ?? "", r.Note, r.Quantity, r.UnitPriceMinor, r.DiscountMinor ?? 0, vat, false));
            }
        }
        return result;
    }

    public async Task<Result<SalesCustomer?>> CustomerAsync(Guid? customerId, SalesCustomerRequest? entered, bool required, CancellationToken ct)
    {
        if (customerId is { } id)
        {
            var found = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == tenant.RequiredOrganizationId, ct);
            if (found is null)
                return Error.NotFound("pos.customer_not_found", "Customer not found.");
            return SalesCustomer.From(found);
        }
        if (entered is null)
            return required ? Error.Validation("pos.sales_customer_required", "Choose a customer or enter one.") : (SalesCustomer?)null;
        var created = SalesCustomer.Create(null, entered.Name, entered.Company, entered.Street, entered.PostalCode, entered.City, entered.CountryCode,
            entered.VatId);
        return created.IsSuccess ? created.Value : created.Error!;
    }

    private static string Describe(string? given, ProductSummary? product) =>
        !string.IsNullOrWhiteSpace(given) ? given.Trim() : product is null ? "Artikel" : $"{product.Brand} {product.Name}".Trim();
}

// ---- quotations -------------------------------------------------------------------------------------------------------------

internal sealed class QuotationService(
    HandyNetDbContext db,
    ITenantContext tenant,
    PosActor actor,
    SalesInputResolver resolver,
    IOrganizationDirectory organizations,
    IOrganizationBranding branding,
    IUserAccounts users,
    IClock clock)
{
    public const int DefaultValidityDays = 30;

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<QuotationDto>> CreateAsync(QuotationRequest request, CancellationToken ct)
    {
        var details = await DetailsAsync(request, BerlinTime.Today(clock.UtcNow), ct);
        if (!details.IsSuccess)
            return details.Error!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var today = BerlinTime.Today(clock.UtcNow);
        var sequence = await DocumentNumbers.QuotationAsync(db, OrganizationId, today.Year, ct);
        var quotation = Quotation.Create(OrganizationId, today.Year, sequence, today, details.Value, actor.UserId, clock.UtcNow);
        if (!quotation.IsSuccess)
            return quotation.Error!;
        db.Add(quotation.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(quotation.Value, ct);
    }

    public async Task<Result<QuotationDto>> UpdateAsync(Guid id, QuotationRequest request, CancellationToken ct)
    {
        var quotation = await FindAsync(id, ct);
        if (quotation is null)
            return NotFound();
        var details = await DetailsAsync(request, quotation.IssueDate, ct);
        if (!details.IsSuccess)
            return details.Error!;
        var updated = quotation.Update(details.Value, clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        return await SaveAsync(quotation, ct);
    }

    public async Task<Result<QuotationDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<Quotation>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.OrganizationId == OrganizationId, ct) is { } q
            ? await ToDtoAsync(q, ct)
            : NotFound();

    public async Task<Result<PagedResult<QuotationSummaryDto>>> ListAsync(SalesQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var today = BerlinTime.Today(clock.UtcNow);
        var query = db.Set<Quotation>().AsNoTracking().Where(x => x.OrganizationId == OrganizationId);
        if (q.CustomerId is { } customerId)
            query = query.Where(x => x.CustomerId == customerId);
        if (q.From is { } from)
            query = query.Where(x => x.IssueDate >= from);
        if (q.To is { } to)
            query = query.Where(x => x.IssueDate <= to);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            if (q.Search.Trim().Length > 100)
                return Error.Validation("pos.search", "The search is limited to 100 characters.");
            var pattern = Text.LikePattern(q.Search);
            query = query.Where(x => EF.Functions.ILike(x.Number, pattern) || (x.CustomerName != null && EF.Functions.ILike(x.CustomerName, pattern))
                                     || (x.CustomerCompany != null && EF.Functions.ILike(x.CustomerCompany, pattern)));
        }
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var status = q.Status.Trim();
            if (string.Equals(status, "Expired", StringComparison.OrdinalIgnoreCase))
                query = query.Where(x => (x.Status == QuotationStatus.Draft || x.Status == QuotationStatus.Sent) && x.ValidUntil < today);
            else if (Enum.TryParse<QuotationStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                query = parsed is QuotationStatus.Draft or QuotationStatus.Sent
                    ? query.Where(x => x.Status == parsed && x.ValidUntil >= today)
                    : query.Where(x => x.Status == parsed);
            else
                return Error.Validation("pos.quotation_status", $"Unknown status '{q.Status}'.");
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.IssueDate).ThenByDescending(x => x.SequenceNumber).Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);
        return new PagedResult<QuotationSummaryDto>(rows.Select(x => new QuotationSummaryDto(x.Id, x.Number, x.DisplayStatus(today), x.Customer.Name,
            x.Customer.Company, x.IssueDate, x.ValidUntil, x.TotalGrossMinor, x.ConvertedInvoiceId)).ToList(), page, size, total);
    }

    public async Task<Result<QuotationDto>> SetStatusAsync(Guid id, QuotationStatusRequest request, CancellationToken ct)
    {
        var quotation = await FindAsync(id, ct);
        if (quotation is null)
            return NotFound();
        var changed = quotation.SetStatus(request.Status, clock.UtcNow);
        return changed.IsSuccess ? await SaveAsync(quotation, ct) : changed.Error!;
    }

    public async Task<Result<QuotationDto>> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Set<Quotation>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.OrganizationId == OrganizationId, ct);
        if (source is null)
            return NotFound();
        var today = BerlinTime.Today(clock.UtcNow);
        var validity = Math.Clamp(source.ValidUntil.DayNumber - source.IssueDate.DayNumber, 0, Quotation.MaxValidityDays);
        var details = new QuotationDetails(source.Customer, today.AddDays(validity), source.PriceMode, ToInputs(source.Lines), source.Notes);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sequence = await DocumentNumbers.QuotationAsync(db, OrganizationId, today.Year, ct);
        var copy = Quotation.Create(OrganizationId, today.Year, sequence, today, details, actor.UserId, clock.UtcNow);
        if (!copy.IsSuccess)
            return copy.Error!;
        db.Add(copy.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(copy.Value, ct);
    }

    /// <summary>
    /// Converts the quotation into an invoice draft (same customer, lines, price mode, notes) and marks it accepted. Converting
    /// again returns the same draft (the quotation row is locked; one invoice per quotation).
    /// </summary>
    public async Task<Result<SalesInvoiceResult>> ConvertAsync(Guid id, SalesInvoiceService invoices, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Set<Quotation>()
            .FromSql($"SELECT *, xmin FROM pos.quotations WHERE id = {id} AND organization_id = {OrganizationId} FOR UPDATE").ToListAsync(ct);
        if (rows.FirstOrDefault() is not { } quotation)
            return NotFound();
        if (quotation.ConvertedInvoiceId is { } existing)
            return (await invoices.GetAsync(existing, ct)).Map(i => new SalesInvoiceResult(i, Existing: true));

        var details = new SalesInvoiceDetails(quotation.Customer, null, await invoices.DefaultPaymentTermsAsync(ct), quotation.PriceMode, false,
            await invoices.DefaultWarehouseAsync(ct), ToInputs(quotation.Lines), quotation.Notes);
        var invoice = SalesInvoice.CreateDraft(OrganizationId, details, quotation.Id, actor.UserId, clock.UtcNow);
        if (!invoice.IsSuccess)
            return invoice.Error!;
        var converted = quotation.MarkConverted(invoice.Value.Id, clock.UtcNow);
        if (!converted.IsSuccess)
            return converted.Error!;
        db.Add(invoice.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await invoices.GetAsync(invoice.Value.Id, ct)).Map(i => new SalesInvoiceResult(i, Existing: false));
    }

    public async Task<Result<SalesEmailDto>> EmailAsync(Guid id, SalesEmailRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quotation = await FindAsync(id, ct);
        if (quotation is null)
            return NotFound();
        var email = await SalesEmails.RecipientAsync(db, request.Email, quotation.CustomerId, ct);
        if (!email.IsSuccess)
            return email.Error!;
        var document = quotation.ToDocument(await SellerAsync(ct));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"sales-email:" + id}, 0))", ct);
        var sent = await db.Set<SalesDocumentEmail>().CountAsync(e => e.DocumentId == id, ct);
        var created = SalesDocumentEmail.Request(OrganizationId, SalesDocumentType.Quotation, id, document, email.Value, sent, SalesDocumentPdf.Render(document, await LetterheadAsync(ct)),
            actor.UserId, clock.UtcNow);
        if (!created.IsSuccess)
            return created.Error!;
        quotation.MarkSent(clock.UtcNow);
        db.Add(created.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SalesEmailDto(created.Value.Id, EReceiptService.Mask(created.Value.Recipient), created.Value.RequestedAt);
    }

    public async Task<Result<InvoiceFile>> PdfAsync(Guid id, CancellationToken ct)
    {
        var quotation = await db.Set<Quotation>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.OrganizationId == OrganizationId, ct);
        if (quotation is null)
            return NotFound();
        return new InvoiceFile($"{quotation.Number}.pdf", SalesDocumentPdf.Render(quotation.ToDocument(await SellerAsync(ct)), await LetterheadAsync(ct)));
    }

    internal static List<SalesLineInput> ToInputs(IEnumerable<SalesLine> lines) =>
        lines.Select(l => new SalesLineInput(l.ProductId, l.StockUnitId, l.Imei, l.Description, l.Note, Math.Abs(l.Quantity), l.UnitPriceMinor,
            Math.Abs(l.DiscountMinor), l.VatRateBasisPoints, l.Margin25a)).ToList();

    private async Task<Result<QuotationDetails>> DetailsAsync(QuotationRequest request, DateOnly issueDate, CancellationToken ct)
    {
        var customer = await resolver.CustomerAsync(request.CustomerId, request.Customer, required: true, ct);
        if (!customer.IsSuccess)
            return customer.Error!;
        var lines = await resolver.LinesAsync(request.Lines, ct);
        if (!lines.IsSuccess)
            return lines.Error!;
        return new QuotationDetails(customer.Value!, request.ValidUntil ?? issueDate.AddDays(DefaultValidityDays), request.PriceMode ?? PriceMode.Gross,
            lines.Value, request.Notes);
    }

    private async Task<SellerInfo> SellerAsync(CancellationToken ct) => await SalesEmails.SellerAsync(organizations, OrganizationId, ct);

    private async Task<DocumentLetterhead> LetterheadAsync(CancellationToken ct) => (await branding.GetDocumentBrandingAsync(OrganizationId, ct)).Letterhead;

    private Task<Quotation?> FindAsync(Guid id, CancellationToken ct) =>
        db.Set<Quotation>().FirstOrDefaultAsync(q => q.Id == id && q.OrganizationId == OrganizationId, ct);

    private async Task<Result<QuotationDto>> SaveAsync(Quotation quotation, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The quotation was changed concurrently. Reload and try again.");
        }
        return await ToDtoAsync(quotation, ct);
    }

    private static Error NotFound() => Error.NotFound("pos.quotation_not_found", "Quotation not found.");

    private async Task<QuotationDto> ToDtoAsync(Quotation q, CancellationToken ct)
    {
        var today = BerlinTime.Today(clock.UtcNow);
        var invoiceNumber = q.ConvertedInvoiceId is { } invoiceId
            ? await db.Set<SalesInvoice>().AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.Number).FirstOrDefaultAsync(ct)
            : null;
        var name = (await users.GetAsync([q.CreatedBy], ct)).GetValueOrDefault(q.CreatedBy)?.DisplayName;
        return new QuotationDto(q.Id, q.Number, q.DisplayStatus(today), q.Status.ToString(), q.IsEditable, q.CustomerId, q.Customer, q.IssueDate, q.ValidUntil,
            q.PriceMode.ToString(), q.Lines, q.Vat, q.MarginGrossMinor, q.TotalNetMinor, q.TotalVatMinor, q.TotalGrossMinor, q.Notes, q.ConvertedInvoiceId,
            invoiceNumber, q.SentAt, q.CreatedAt, q.CreatedBy, name, q.UpdatedAt, $"/api/pos/quotations/{q.Id}/pdf");
    }
}

internal static class SalesEmails
{
    public static async Task<Result<string>> RecipientAsync(HandyNetDbContext db, string? given, Guid? customerId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return given.Trim();
        var email = customerId is { } id
            ? await db.Set<Customer>().AsNoTracking().Where(c => c.Id == id).Select(c => c.Email).FirstOrDefaultAsync(ct)
            : null;
        return string.IsNullOrWhiteSpace(email)
            ? Error.Validation("pos.sales_email_missing", "Enter an email address (the customer has no email).")
            : email;
    }

    public static async Task<SellerInfo> SellerAsync(IOrganizationDirectory organizations, Guid organizationId, CancellationToken ct)
    {
        var legal = await organizations.GetLegalDetailsAsync(organizationId, ct) ?? throw new InvalidOperationException("Organization not found.");
        return new SellerInfo(legal.Name, legal.LegalName, legal.Street, legal.PostalCode, legal.City, legal.CountryCode, legal.VatId);
    }
}

// ---- sales invoices ---------------------------------------------------------------------------------------------------------

internal sealed class SalesInvoiceService(
    HandyNetDbContext db,
    ITenantContext tenant,
    PosActor actor,
    SalesInputResolver resolver,
    IInventory inventory,
    IOrganizationDirectory organizations,
    IOrganizationBranding branding,
    IUserAccounts users,
    ApprovalService approvals,
    IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>Payment term of the company profile (Zahlungsziel), otherwise 14 days.</summary>
    public async Task<int> DefaultPaymentTermsAsync(CancellationToken ct) =>
        (await branding.GetDocumentBrandingAsync(OrganizationId, ct)).PaymentTermsDays ?? SalesInvoice.DefaultPaymentTermDays;

    private async Task<DocumentLetterhead> LetterheadAsync(CancellationToken ct) => (await branding.GetDocumentBrandingAsync(OrganizationId, ct)).Letterhead;

    public async Task<Guid?> DefaultWarehouseAsync(CancellationToken ct) =>
        (await inventory.GetWarehousesAsync(ct)).FirstOrDefault()?.Id;

    public async Task<IReadOnlyList<SalesWarehouseDto>> WarehousesAsync(CancellationToken ct) =>
        (await inventory.GetWarehousesAsync(ct)).Select(w => new SalesWarehouseDto(w.Id, w.Code, w.Name, w.IsDefault)).ToList();

    public async Task<Result<SalesInvoiceDto>> CreateAsync(SalesInvoiceRequest request, CancellationToken ct)
    {
        var details = await DetailsAsync(request, ct);
        if (!details.IsSuccess)
            return details.Error!;
        var invoice = SalesInvoice.CreateDraft(OrganizationId, details.Value, null, actor.UserId, clock.UtcNow);
        if (!invoice.IsSuccess)
            return invoice.Error!;
        db.Add(invoice.Value);
        await db.SaveChangesAsync(ct);
        return await GetAsync(invoice.Value.Id, ct);
    }

    public async Task<Result<SalesInvoiceDto>> UpdateAsync(Guid id, SalesInvoiceRequest request, CancellationToken ct)
    {
        var invoice = await db.Set<SalesInvoice>().FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (invoice is null)
            return NotFound();
        var details = await DetailsAsync(request, ct);
        if (!details.IsSuccess)
            return details.Error!;
        var updated = invoice.UpdateDraft(details.Value, clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        return await SaveAndGetAsync(invoice, ct);
    }

    public async Task<Result<SalesInvoiceDto>> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Set<SalesInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (source is null || source.Kind != SalesInvoiceKind.Invoice)
            return NotFound();
        var invoice = SalesInvoice.CreateDraft(OrganizationId, source.ToDetails(), null, actor.UserId, clock.UtcNow);
        if (!invoice.IsSuccess)
            return invoice.Error!;
        db.Add(invoice.Value);
        await db.SaveChangesAsync(ct);
        return await GetAsync(invoice.Value.Id, ct);
    }

    public async Task<Result<SalesInvoiceDto>> DiscardAsync(Guid id, CancellationToken ct)
    {
        var invoice = await db.Set<SalesInvoice>().FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (invoice is null)
            return NotFound();
        var discarded = invoice.Discard(clock.UtcNow);
        return discarded.IsSuccess ? await SaveAndGetAsync(invoice, ct) : discarded.Error!;
    }

    /// <summary>
    /// Issues a draft in one transaction: draft row locked (a second request waits and then gets the issued invoice — idempotent),
    /// customer data refreshed from the POS customer, all checks before the number, stock staged (reserved stock is never sold,
    /// IMEI units InStock → Sold, §25a internals from the unit), next number of the shared series, immutable document.
    /// </summary>
    public async Task<Result<SalesInvoiceResult>> IssueAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invoice = await LockAsync(id, ct);
        if (invoice is null)
            return NotFound();
        if (invoice.Status == SalesInvoiceStatus.Issued)
            return (await GetAsync(id, ct)).Map(i => new SalesInvoiceResult(i, Existing: true));
        if (invoice.Status != SalesInvoiceStatus.Draft)
            return Error.Conflict("pos.sales_invoice_discarded", "The draft was discarded.");

        if (invoice.CustomerId is { } customerId
            && await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct) is { } master)
        {
            var refreshed = invoice.UpdateDraft(invoice.ToDetails() with { Customer = SalesCustomer.From(master), ServiceDate = invoice.ServiceDate }, clock.UtcNow);
            if (!refreshed.IsSuccess)
                return refreshed.Error!;
        }
        var seller = await SalesEmails.SellerAsync(organizations, OrganizationId, ct);
        var check = invoice.CheckIssuable(seller);
        if (!check.IsSuccess)
            return check.Error!;
        if (invoice.WarehouseId is { } warehouse && !await inventory.IsOwnWarehouseAsync(warehouse, ct))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");

        // The number first: the series row stays locked until commit, so issues of one organization run one after another (no
        // stock races between them); any refusal below rolls the number back with everything else.
        var today = BerlinTime.Today(clock.UtcNow);
        var sequence = await DocumentNumbers.InvoiceAsync(db, OrganizationId, today.Year, ct);
        var reference = $"sales-invoice:{invoice.Id}";
        var margins = new List<MarginDetail>();
        foreach (var line in invoice.Lines.Where(l => !l.IsFreeText))
        {
            if (line.Imei is { } imei)
            {
                var unit = await inventory.StageSellUnitAsync(invoice.WarehouseId!.Value, imei, reference, ct);
                if (!unit.IsSuccess)
                    return unit.Error!;
                if (unit.Value.ProductId != line.ProductId || unit.Value.UnitId != line.StockUnitId)
                    return Error.Conflict("pos.sales_unit_changed", $"Line {line.Position}: the device {imei} changed. Edit the line again.");
                var margin = unit.Value.TaxScheme == Inventory.Domain.TaxScheme.Margin25a;
                if (margin != line.Margin25a)
                    return Error.Conflict("pos.sales_unit_changed", $"Line {line.Position}: the tax scheme of device {imei} changed. Edit the line again.");
                if (margin)
                    margins.Add(new MarginDetail(line.Position, unit.Value.UnitId, unit.Value.PurchasePriceMinor,
                        VatCalculator.MarginVat(line.AmountMinor, unit.Value.PurchasePriceMinor)));
            }
            else
            {
                var sold = await inventory.StageSellAsync(invoice.WarehouseId!.Value, line.ProductId!.Value, line.Quantity, reference, ct);
                if (!sold.IsSuccess)
                    return sold.Error!;
            }
        }

        var issued = invoice.Issue(seller, today.Year, sequence, today, margins, actor.UserId, clock.UtcNow);
        if (!issued.IsSuccess)
            return issued.Error!;
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("pos.stock_changed", "Stock changed while issuing (e.g. the device was sold at the till). Try again.");
        }
        return (await GetAsync(id, ct)).Map(i => new SalesInvoiceResult(i, Existing: false));
    }

    /// <summary>
    /// Stornorechnung: the negative mirror of an issued invoice in the same series; stock (and IMEI units) back to the
    /// invoice's warehouse. Needs pos.refund or a manager approval (X-Pos-Approval, bound to a register). Repeat = the same note.
    /// </summary>
    public async Task<Result<SalesInvoiceResult>> CreditNoteAsync(Guid id, CreditNoteRequest request, CancellationToken ct)
    {
        if (actor.Approval is not null)
        {
            if (request.RegisterId is not { } registerId)
                return Error.Forbidden("pos.approval_invalid", "An approval is bound to a register; send the registerId with the credit note.");
            if (actor.CheckRegister(registerId) is { } other)
                return other;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var original = await LockAsync(id, ct);
        if (original is null)
            return NotFound();
        if (await db.Set<SalesInvoice>().AsNoTracking().Where(c => c.OriginalInvoiceId == id).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct) is { } existing)
            return (await GetAsync(existing, ct)).Map(i => new SalesInvoiceResult(i, Existing: true));
        if (await approvals.LockAsync(actor, ct) is { } approvalUnusable)
            return approvalUnusable;

        var seller = await SalesEmails.SellerAsync(organizations, OrganizationId, ct);
        var today = BerlinTime.Today(clock.UtcNow);
        // Checks first (no number is taken for a refusal).
        var probe = SalesInvoice.CreditNoteFor(original, seller, today.Year, 1, today, request.Reason, actor.UserId, clock.UtcNow);
        if (!probe.IsSuccess)
            return probe.Error!;

        var reference = $"sales-invoice:{original.Id}:storno";
        foreach (var line in original.Lines.Where(l => !l.IsFreeText))
        {
            var restocked = line.StockUnitId is { } unitId
                ? await inventory.StageReturnUnitAsync(unitId, reference, ct)
                : await inventory.StageReturnAsync(original.WarehouseId!.Value, line.ProductId!.Value, line.Quantity, reference, ct);
            if (!restocked.IsSuccess)
                return restocked.Error!;
        }
        var sequence = await DocumentNumbers.InvoiceAsync(db, OrganizationId, today.Year, ct);
        var note = SalesInvoice.CreditNoteFor(original, seller, today.Year, sequence, today, request.Reason, actor.UserId, clock.UtcNow);
        if (!note.IsSuccess)
            return note.Error!;
        db.Add(note.Value);
        try
        {
            await db.SaveChangesAsync(ct);
            if (await approvals.ConsumeAsync(actor, ApprovalUse.CreditNote, null, null, null, ct, note.Value.Id) is { } approvalError)
                return approvalError;
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_sales_invoices_one_credit_note"))
        {
            return Error.Conflict("pos.sales_invoice_cancelled", "The invoice was already cancelled.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_stock_units_imei_in_stock"))
        {
            return Error.Conflict("unit.imei_in_stock", "A device of this invoice is already in stock again. Check its origin.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("pos.stock_changed", "Stock changed concurrently. Try again.");
        }
        return (await GetAsync(note.Value.Id, ct)).Map(i => new SalesInvoiceResult(i, Existing: false));
    }

    /// <summary>Records a bank transfer / card / other payment (cash in the shop goes through the till).</summary>
    public async Task<Result<SalesInvoiceDto>> RecordPaymentAsync(Guid id, InvoicePaymentRequest request, CancellationToken ct)
    {
        if (request.Method == InvoicePaymentMethod.Cash)
            return Error.Validation("pos.cash_payment_at_till", "Cash received in the shop is collected at the till (\"An der Kasse kassieren\").");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invoice = await LockAsync(id, ct);
        if (invoice is null)
            return NotFound();
        var (paid, cancelled) = await PaymentStateAsync(id, ct);
        var payment = SalesInvoicePayment.Record(invoice, request.Method, request.AmountMinor, request.PaidOn ?? BerlinTime.Today(clock.UtcNow),
            request.Reference, paid, cancelled, null, actor.UserId, clock.UtcNow);
        if (!payment.IsSuccess)
            return payment.Error!;
        db.Add(payment.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<SalesEmailDto>> EmailAsync(Guid id, SalesEmailRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invoice = await db.Set<SalesInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (invoice is null)
            return NotFound();
        if (invoice.Document is null)
            return Error.Conflict("pos.sales_invoice_not_issued", "Issue the invoice before sending it.");
        var email = await SalesEmails.RecipientAsync(db, request.Email, invoice.CustomerId, ct);
        if (!email.IsSuccess)
            return email.Error!;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"sales-email:" + id}, 0))", ct);
        var sent = await db.Set<SalesDocumentEmail>().CountAsync(e => e.DocumentId == id, ct);
        var created = SalesDocumentEmail.Request(OrganizationId, SalesDocumentType.Invoice, id, invoice.Document, email.Value, sent,
            SalesDocumentPdf.Render(invoice.Document, await LetterheadAsync(ct)), actor.UserId, clock.UtcNow);
        if (!created.IsSuccess)
            return created.Error!;
        db.Add(created.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SalesEmailDto(created.Value.Id, EReceiptService.Mask(created.Value.Recipient), created.Value.RequestedAt);
    }

    /// <summary>Issued: the stored document. Draft: a preview marked "Entwurf" (no number).</summary>
    public async Task<Result<InvoiceFile>> PdfAsync(Guid id, CancellationToken ct)
    {
        var invoice = await db.Set<SalesInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == OrganizationId, ct);
        if (invoice is null)
            return NotFound();
        if (invoice.Document is { } document)
            return new InvoiceFile($"{document.Number}.pdf", SalesDocumentPdf.Render(document, await LetterheadAsync(ct)));
        var seller = await SalesEmails.SellerAsync(organizations, OrganizationId, ct);
        var today = BerlinTime.Today(clock.UtcNow);
        var preview = new SalesDocument("Invoice", "Rechnung (Entwurf)", "ENTWURF", today, invoice.ServiceDate ?? today,
            today.AddDays(invoice.PaymentTermDays), null, seller, invoice.Customer ?? new SalesCustomer(null, "–", null, null, null, null, null, null),
            invoice.PriceMode, invoice.Lines, invoice.Vat, invoice.MarginGrossMinor, invoice.TotalNetMinor, invoice.TotalVatMinor, invoice.TotalGrossMinor,
            null, ["Entwurf – keine gültige Rechnung."], invoice.Notes);
        return new InvoiceFile("Rechnung-Entwurf.pdf", SalesDocumentPdf.Render(preview, await LetterheadAsync(ct)));
    }

    public async Task<Result<SalesInvoiceDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var i = await db.Set<SalesInvoice>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == OrganizationId, ct);
        if (i is null)
            return NotFound();
        var payments = await db.Set<SalesInvoicePayment>().AsNoTracking().Where(p => p.InvoiceId == id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var receiptIds = payments.Where(p => p.ReceiptId != null).Select(p => p.ReceiptId!.Value).ToList();
        var receiptNumbers = await db.Set<Receipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var creditNote = await db.Set<SalesInvoice>().AsNoTracking().Where(c => c.OriginalInvoiceId == id)
            .Select(c => new { c.Id, c.Number }).FirstOrDefaultAsync(ct);
        var original = i.OriginalInvoiceId is { } originalId
            ? await db.Set<SalesInvoice>().AsNoTracking().Where(c => c.Id == originalId).Select(c => c.Number).FirstOrDefaultAsync(ct)
            : null;
        var quotationNumber = i.QuotationId is { } quotationId
            ? await db.Set<Quotation>().AsNoTracking().Where(q => q.Id == quotationId).Select(q => q.Number).FirstOrDefaultAsync(ct)
            : null;
        var people = await users.GetAsync(payments.Select(p => p.CreatedBy).Append(i.CreatedBy).Distinct().ToList(), ct);
        var paid = payments.Sum(p => p.AmountMinor);
        var issuedInvoice = i.Kind == SalesInvoiceKind.Invoice && i.Status == SalesInvoiceStatus.Issued;
        var cancelled = creditNote is not null;
        var state = issuedInvoice ? SalesCalculator.StateOf(i.TotalGrossMinor, paid).ToString() : null;
        var overdue = issuedInvoice && !cancelled && paid < i.TotalGrossMinor && i.DueDate < BerlinTime.Today(clock.UtcNow);
        var warnings = new List<string>();
        if (i.Status == SalesInvoiceStatus.Draft)
        {
            if (i.Customer is { IsBusiness: true, VatId: null })
                warnings.Add("customer_vat_id_missing");
            if (i.ReverseCharge)
                warnings.Add("reverse_charge_check");
        }
        return new SalesInvoiceDto(i.Id, i.Kind.ToString(), i.Status.ToString(), i.Number, i.CustomerId, i.Customer, i.InvoiceDate, i.ServiceDate,
            i.PaymentTermDays, i.DueDate, i.PriceMode.ToString(), i.ReverseCharge, i.WarehouseId, i.QuotationId, quotationNumber, i.OriginalInvoiceId, original,
            creditNote?.Id, creditNote?.Number, i.Reason, i.Notes, i.Lines, i.Vat, i.MarginGrossMinor, i.TotalNetMinor, i.TotalVatMinor, i.TotalGrossMinor,
            paid, issuedInvoice && !cancelled ? Math.Max(0, i.TotalGrossMinor - paid) : 0, state, overdue, cancelled,
            payments.Select(p => new InvoicePaymentDto(p.Id, p.Method.ToString(), p.AmountMinor, p.PaidOn, p.ReceiptId,
                p.ReceiptId is { } r ? receiptNumbers.GetValueOrDefault(r) : null, p.Reference, p.CreatedAt, p.CreatedBy,
                people.GetValueOrDefault(p.CreatedBy)?.DisplayName)).ToList(),
            i.IssuedAt, i.CreatedAt, i.CreatedBy, people.GetValueOrDefault(i.CreatedBy)?.DisplayName, $"/api/pos/sales-invoices/{i.Id}/pdf", warnings);
    }

    /// <summary>
    /// All invoices of the organization — issued here (drafts, invoices, credit notes) and invoices for POS sales (origin
    /// "Receipt", paid at the till) — newest first. Filters: search (number, customer), kind, status, payment state, overdue, dates.
    /// </summary>
    public async Task<Result<PagedResult<InvoiceListItemDto>>> ListAsync(SalesQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var today = BerlinTime.Today(clock.UtcNow);
        var pattern = string.IsNullOrWhiteSpace(q.Search) ? null : Text.LikePattern(q.Search.Length > 100 ? q.Search[..100] : q.Search);

        SalesInvoiceKind? kind = null;
        if (!string.IsNullOrWhiteSpace(q.Kind))
        {
            if (!Enum.TryParse<SalesInvoiceKind>(q.Kind.Trim(), true, out var k) || !Enum.IsDefined(k))
                return Error.Validation("pos.sales_invoice_kind", $"Unknown kind '{q.Kind}'.");
            kind = k;
        }
        SalesInvoiceStatus? status = null;
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            if (!Enum.TryParse<SalesInvoiceStatus>(q.Status.Trim(), true, out var s) || !Enum.IsDefined(s))
                return Error.Validation("pos.sales_invoice_status", $"Unknown status '{q.Status}'.");
            status = s;
        }
        PaymentState? paymentState = null;
        if (!string.IsNullOrWhiteSpace(q.PaymentState))
        {
            if (!Enum.TryParse<PaymentState>(q.PaymentState.Trim(), true, out var p) || !Enum.IsDefined(p))
                return Error.Validation("pos.payment_state", $"Unknown payment state '{q.PaymentState}'.");
            paymentState = p;
        }

        var invoices = db.Set<SalesInvoice>().AsNoTracking().Where(i => i.OrganizationId == OrganizationId);
        if (kind is { } wantedKind)
            invoices = invoices.Where(i => i.Kind == wantedKind);
        if (status is { } wantedStatus)
            invoices = invoices.Where(i => i.Status == wantedStatus);
        else
            invoices = invoices.Where(i => i.Status != SalesInvoiceStatus.Discarded);
        if (q.CustomerId is { } customerId)
            invoices = invoices.Where(i => i.CustomerId == customerId);
        if (q.From is { } from)
            invoices = invoices.Where(i => (i.InvoiceDate ?? DateOnly.MinValue) >= from);
        if (q.To is { } to)
            invoices = invoices.Where(i => (i.InvoiceDate ?? DateOnly.MinValue) <= to);
        if (pattern is not null)
            invoices = invoices.Where(i => (i.Number != null && EF.Functions.ILike(i.Number, pattern))
                                           || (i.CustomerName != null && EF.Functions.ILike(i.CustomerName, pattern))
                                           || (i.CustomerCompany != null && EF.Functions.ILike(i.CustomerCompany, pattern)));
        var rows = invoices.Select(i => new
        {
            i.Id, i.Kind, i.Status, i.Number, i.CustomerName, i.CustomerCompany, i.InvoiceDate, i.DueDate, i.TotalGrossMinor,
            SortAt = i.IssuedAt ?? i.CreatedAt,
            Paid = db.Set<SalesInvoicePayment>().Where(p => p.InvoiceId == i.Id).Sum(p => (long?)p.AmountMinor) ?? 0,
            Cancelled = db.Set<SalesInvoice>().Any(c => c.OriginalInvoiceId == i.Id),
        });
        var openable = rows.Where(r => r.Kind == SalesInvoiceKind.Invoice && r.Status == SalesInvoiceStatus.Issued && !r.Cancelled);
        if (paymentState == PaymentState.Paid)
            rows = rows.Where(r => r.Kind == SalesInvoiceKind.Invoice && r.Status == SalesInvoiceStatus.Issued && r.Paid >= r.TotalGrossMinor);
        else if (paymentState == PaymentState.PartiallyPaid)
            rows = openable.Where(r => r.Paid > 0 && r.Paid < r.TotalGrossMinor);
        else if (paymentState == PaymentState.Open)
            rows = openable.Where(r => r.Paid == 0);
        if (q.Overdue == true)
            rows = rows.Where(r => r.Kind == SalesInvoiceKind.Invoice && r.Status == SalesInvoiceStatus.Issued && !r.Cancelled && r.Paid < r.TotalGrossMinor
                                   && r.DueDate < today);

        // Invoices for POS sales: issued, paid at the till, never overdue; only where the filters can match them.
        var includeReceipts = (kind is null or SalesInvoiceKind.Invoice) && (status is null or SalesInvoiceStatus.Issued)
                                                                        && (paymentState is null or PaymentState.Paid) && q.Overdue != true;
        var receiptInvoices = includeReceipts ? ReceiptInvoices(q, pattern) : null;

        var take = page * size;
        var total = await rows.CountAsync(ct) + (receiptInvoices is null ? 0 : await receiptInvoices.CountAsync(ct));
        var sales = (await rows.OrderByDescending(r => r.SortAt).Take(take).ToListAsync(ct))
            .Select(r => new InvoiceListItemDto(r.Id, "Sale", r.Kind.ToString(), r.Status.ToString(), r.Number, r.CustomerName, r.CustomerCompany, r.InvoiceDate,
                r.DueDate, r.TotalGrossMinor, r.Paid,
                r.Kind == SalesInvoiceKind.Invoice && r.Status == SalesInvoiceStatus.Issued ? SalesCalculator.StateOf(r.TotalGrossMinor, r.Paid).ToString() : null,
                r.Kind == SalesInvoiceKind.Invoice && r.Status == SalesInvoiceStatus.Issued && !r.Cancelled && r.Paid < r.TotalGrossMinor && r.DueDate < today,
                r.Cancelled, null, r.SortAt));
        var fromReceipts = receiptInvoices is null
            ? []
            : (await receiptInvoices.OrderByDescending(r => r.IssuedAt).Take(take).ToListAsync(ct))
            .Select(r => new InvoiceListItemDto(r.Id, "Receipt", "Invoice", "Issued", r.Number, r.Customer.Name, r.Customer.Company, r.IssueDate, r.IssueDate,
                r.TotalGrossMinor, r.TotalGrossMinor, PaymentState.Paid.ToString(), false, false, r.ReceiptId, r.IssuedAt));
        var items = sales.Concat(fromReceipts).OrderByDescending(x => x.SortAt).ThenByDescending(x => x.Number).Skip((page - 1) * size).Take(size).ToList();
        return new PagedResult<InvoiceListItemDto>(items, page, size, total);
    }

    public async Task<SalesInvoiceSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var today = BerlinTime.Today(clock.UtcNow);
        var open = await db.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.OrganizationId == OrganizationId && i.Kind == SalesInvoiceKind.Invoice && i.Status == SalesInvoiceStatus.Issued
                        && !db.Set<SalesInvoice>().Any(c => c.OriginalInvoiceId == i.Id))
            .Select(i => new
            {
                i.DueDate,
                Open = i.TotalGrossMinor - (db.Set<SalesInvoicePayment>().Where(p => p.InvoiceId == i.Id).Sum(p => (long?)p.AmountMinor) ?? 0),
            })
            .Where(x => x.Open > 0).ToListAsync(ct);
        var drafts = await db.Set<SalesInvoice>().CountAsync(i => i.OrganizationId == OrganizationId && i.Status == SalesInvoiceStatus.Draft, ct);
        var overdue = open.Where(x => x.DueDate < today).ToList();
        return new SalesInvoiceSummaryDto(overdue.Count, overdue.Sum(x => x.Open), open.Count, open.Sum(x => x.Open), drafts);
    }

    /// <summary>Paid amount and whether a credit note exists (read under the invoice row lock).</summary>
    internal async Task<(long Paid, bool Cancelled)> PaymentStateAsync(Guid invoiceId, CancellationToken ct) =>
        (await db.Set<SalesInvoicePayment>().Where(p => p.InvoiceId == invoiceId).SumAsync(p => (long?)p.AmountMinor, ct) ?? 0,
            await db.Set<SalesInvoice>().AnyAsync(c => c.OriginalInvoiceId == invoiceId, ct));

    /// <summary>Locks the invoice row (FOR UPDATE) for issue, credit note and payments; tracked.</summary>
    internal async Task<SalesInvoice?> LockAsync(Guid id, CancellationToken ct) =>
        (await db.Set<SalesInvoice>()
            .FromSql($"SELECT *, xmin FROM pos.sales_invoices WHERE id = {id} AND organization_id = {OrganizationId} FOR UPDATE").ToListAsync(ct))
        .FirstOrDefault();

    private IQueryable<PosInvoice> ReceiptInvoices(SalesQuery q, string? pattern)
    {
        var query = db.Set<PosInvoice>().AsNoTracking().Where(i => i.OrganizationId == OrganizationId);
        if (q.From is { } from)
            query = query.Where(i => i.IssueDate >= from);
        if (q.To is { } to)
            query = query.Where(i => i.IssueDate <= to);
        if (q.CustomerId is { } customerId)
            query = query.Where(i => db.Set<Receipt>().Any(r => r.Id == i.ReceiptId && r.CustomerId == customerId));
        if (pattern is not null)
            query = query.Where(i => EF.Functions.ILike(i.Number, pattern) || (i.CustomerName != null && EF.Functions.ILike(i.CustomerName, pattern))
                                     || (i.CustomerCompany != null && EF.Functions.ILike(i.CustomerCompany, pattern)));
        return query;
    }

    private async Task<Result<SalesInvoiceDetails>> DetailsAsync(SalesInvoiceRequest request, CancellationToken ct)
    {
        var customer = await resolver.CustomerAsync(request.CustomerId, request.Customer, required: false, ct);
        if (!customer.IsSuccess)
            return customer.Error!;
        var lines = await resolver.LinesAsync(request.Lines, ct);
        if (!lines.IsSuccess)
            return lines.Error!;
        var warehouse = request.WarehouseId;
        if (warehouse is { } w && !await inventory.IsOwnWarehouseAsync(w, ct))
            return Error.NotFound("warehouse.not_found", "Warehouse not found.");
        warehouse ??= await DefaultWarehouseAsync(ct);
        return new SalesInvoiceDetails(customer.Value, request.ServiceDate, request.PaymentTermDays ?? await DefaultPaymentTermsAsync(ct),
            request.PriceMode ?? PriceMode.Gross, request.ReverseCharge ?? false, warehouse, lines.Value, request.Notes);
    }

    private async Task<Result<SalesInvoiceDto>> SaveAndGetAsync(SalesInvoice invoice, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The invoice was changed concurrently. Reload and try again.");
        }
        return await GetAsync(invoice.Id, ct);
    }

    private static Error NotFound() => Error.NotFound("pos.sales_invoice_not_found", "Invoice not found.");
}

internal static class ResultMapping
{
    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> map) =>
        result.IsSuccess ? Result<TOut>.Success(map(result.Value)) : Result<TOut>.Failure(result.Error!);
}
