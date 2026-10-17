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
    string? VatId)
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
    DateTimeOffset UpdatedAt);

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

internal sealed class CustomerService(HandyNetDbContext db, ITenantContext tenant, ICurrentUser currentUser, IClock clock)
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
        return new PagedResult<CustomerDto>(items.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId, ct) is { } customer
            ? ToDto(customer)
            : Error.NotFound("pos.customer_not_found", "Customer not found.");

    public async Task<Result<CustomerDto>> CreateAsync(CustomerRequest request, CancellationToken ct)
    {
        var customer = Customer.Create(OrganizationId, request.ToDetails(), currentUser.UserId, clock.UtcNow);
        if (!customer.IsSuccess)
            return customer.Error!;
        db.Add(customer.Value);
        await db.SaveChangesAsync(ct);
        return ToDto(customer.Value);
    }

    public async Task<Result<CustomerDto>> UpdateAsync(Guid id, CustomerRequest request, CancellationToken ct)
    {
        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId, ct);
        if (customer is null)
            return Error.NotFound("pos.customer_not_found", "Customer not found.");
        var updated = customer.Update(request.ToDetails(), clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The customer was changed concurrently. Reload and try again.");
        }
        return ToDto(customer);
    }

    internal static CustomerDto ToDto(Customer c) =>
        new(c.Id, c.Name, c.Company, c.Email, c.Phone, c.Street, c.PostalCode, c.City, c.CountryCode, c.VatId, c.CreatedAt, c.UpdatedAt);
}
