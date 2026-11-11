using FluentValidation;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

public sealed record CustomerRequest(
    string Name,
    string? Company,
    string? Email,
    string? Phone,
    string? Street,
    string? PostalCode,
    string? City,
    string? CountryCode,
    string? VatId,
    Guid? PricelistId = null)
{
    public CustomerDetails ToDetails() => new(Name, Company, Email, Phone, Street, PostalCode, City, CountryCode, VatId);
}

public sealed record CustomerDto(
    Guid Id,
    string Name,
    string? Company,
    string? Email,
    string? Phone,
    string? Street,
    string? PostalCode,
    string? City,
    string? CountryCode,
    string? VatId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? PricelistId = null,
    string? PricelistName = null,
    int LoyaltyPoints = 0);

internal sealed class CustomerValidator : AbstractValidator<CustomerRequest>
{
    public CustomerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Customer.MaxNameLength);
        RuleFor(x => x.Company).MaximumLength(Customer.MaxNameLength);
        RuleFor(x => x.Email).MaximumLength(Customer.MaxEmailLength).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(Customer.MaxPhoneLength);
        RuleFor(x => x.Street).MaximumLength(Customer.MaxStreetLength);
        RuleFor(x => x.PostalCode).MaximumLength(Customer.MaxPostalCodeLength);
        RuleFor(x => x.City).MaximumLength(Customer.MaxCityLength);
        RuleFor(x => x.CountryCode).Length(2).When(x => !string.IsNullOrWhiteSpace(x.CountryCode));
        RuleFor(x => x.VatId).MaximumLength(Customer.MaxVatIdLength + 5);
    }
}

internal sealed class CustomerService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<PagedResult<CustomerDto>> ListAsync(string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Customer>().AsNoTracking().Where(c => c.OrganizationId == OrganizationId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Text.LikePattern(search.Length > 100 ? search[..100] : search);
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern) || (c.Company != null && EF.Functions.ILike(c.Company, pattern))
                                     || (c.Email != null && EF.Functions.ILike(c.Email, pattern)) || (c.Phone != null && EF.Functions.ILike(c.Phone, pattern))
                                     || (c.VatId != null && EF.Functions.ILike(c.VatId, pattern)));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<CustomerDto>(await ToDtosAsync(items, ct), p, size, total);
    }

    public async Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId, ct) is { } customer
            ? (await ToDtosAsync([customer], ct))[0]
            : Error.NotFound("pos.customer_not_found", "Customer not found.");

    public async Task<Result<CustomerDto>> CreateAsync(CustomerRequest request, CancellationToken ct)
    {
        var customer = Customer.Create(OrganizationId, request.ToDetails(), actor.UserId, clock.UtcNow);
        if (!customer.IsSuccess)
            return customer.Error!;
        if (await AssignPricelistAsync(customer.Value, request.PricelistId, ct) is { } refused)
            return refused;
        db.Add(customer.Value);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([customer.Value], ct))[0];
    }

    public async Task<Result<CustomerDto>> UpdateAsync(Guid id, CustomerRequest request, CancellationToken ct)
    {
        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId, ct);
        if (customer is null)
            return Error.NotFound("pos.customer_not_found", "Customer not found.");
        var updated = customer.Update(request.ToDetails(), clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        if (await AssignPricelistAsync(customer, request.PricelistId, ct) is { } refused)
            return refused;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The customer was changed concurrently. Reload and try again.");
        }
        return (await ToDtosAsync([customer], ct))[0];
    }

    /// <summary>
    /// The customer's price list (ADR-0020). Assigning or removing one changes the prices the customer pays, so a change
    /// needs <c>pos.manage</c>; a request that leaves it as it is (e.g. the till editing an address) passes with <c>pos.sell</c>.
    /// </summary>
    private async Task<Error?> AssignPricelistAsync(Customer customer, Guid? pricelistId, CancellationToken ct)
    {
        if (customer.PricelistId == pricelistId)
            return null;
        if (!actor.Has(Permissions.PosManage))
            return Error.Forbidden("permission.denied", $"Missing permission '{Permissions.PosManage}' (changing a customer's price list).");
        if (pricelistId is { } id && !await db.Set<Pricelist>().AnyAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct))
            return Error.NotFound("pos.pricelist_not_found", "Price list not found.");
        customer.SetPricelist(pricelistId, clock.UtcNow);
        return null;
    }

    private async Task<List<CustomerDto>> ToDtosAsync(IReadOnlyList<Customer> customers, CancellationToken ct)
    {
        var pricelistIds = customers.Where(c => c.PricelistId != null).Select(c => c.PricelistId!.Value).Distinct().ToList();
        var names = pricelistIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Pricelist>().AsNoTracking().Where(p => pricelistIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var customerIds = customers.Select(c => c.Id).ToList();
        var points = customerIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.Set<LoyaltyAccount>().AsNoTracking().Where(a => a.OrganizationId == OrganizationId && customerIds.Contains(a.CustomerId))
                .ToDictionaryAsync(a => a.CustomerId, a => a.Points, ct);
        return customers.Select(c => ToDto(c, c.PricelistId is { } id ? names.GetValueOrDefault(id) : null, points.GetValueOrDefault(c.Id))).ToList();
    }

    internal static CustomerDto ToDto(Customer c, string? pricelistName = null, int loyaltyPoints = 0) =>
        new(c.Id, c.Name, c.Company, c.Email, c.Phone, c.Street, c.PostalCode, c.City, c.CountryCode, c.VatId, c.CreatedAt, c.UpdatedAt, c.PricelistId,
            pricelistName, loyaltyPoints);
}
