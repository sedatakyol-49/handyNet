using System.Net.Mail;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

public sealed record CustomerDetails(
    string Name,
    string? Company,
    string? Email,
    string? Phone,
    string? Street,
    string? PostalCode,
    string? City,
    string? CountryCode,
    string? VatId);

/// <summary>
/// A customer of the organization's store, chosen or created at the till (Odoo POS customer). Tenant-owned (RLS).
/// All identifying fields are personal data (<see cref="PersonalDataAttribute"/>): they are never copied into the audit
/// log, so changing or anonymizing the customer really removes them. Customers are never deleted (receipts reference
/// them); issued receipts keep their own snapshot of name/company/address/VAT id as part of the immutable record.
/// </summary>
public sealed class Customer : IAuditable
{
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxPhoneLength = 50;
    public const int MaxStreetLength = 200;
    public const int MaxPostalCodeLength = 10;
    public const int MaxCityLength = 100;
    public const int MaxVatIdLength = 20;

    private Customer() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    [PersonalData]
    public string Name { get; private set; } = null!;

    [PersonalData]
    public string? Company { get; private set; }

    [PersonalData]
    public string? Email { get; private set; }

    [PersonalData]
    public string? Phone { get; private set; }

    [PersonalData]
    public string? Street { get; private set; }

    [PersonalData]
    public string? PostalCode { get; private set; }

    [PersonalData]
    public string? City { get; private set; }

    public string? CountryCode { get; private set; }

    [PersonalData]
    public string? VatId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<Customer> Create(Guid organizationId, CustomerDetails details, Guid userId, DateTimeOffset now)
    {
        var customer = new Customer { Id = Guid.CreateVersion7(), OrganizationId = organizationId, CreatedAt = now, CreatedBy = userId };
        var applied = customer.Update(details, now);
        return applied.IsSuccess ? customer : applied.Error!;
    }

    public Result Update(CustomerDetails d, DateTimeOffset now)
    {
        var name = Clean(d.Name);
        if (name is null || name.Length > MaxNameLength)
            return Error.Validation("pos.customer_name", $"A customer name (max {MaxNameLength} characters) is required.");
        var email = Clean(d.Email);
        if (email is not null && (email.Length > MaxEmailLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email))
            return Error.Validation("pos.customer_email", "The email address is not valid.");
        var country = Clean(d.CountryCode)?.ToUpperInvariant();
        if (country is not null && (country.Length != 2 || !country.All(char.IsAsciiLetterUpper)))
            return Error.Validation("pos.customer_country", "The country must be a two-letter ISO code (e.g. DE).");
        var vatId = Clean(d.VatId) is { } v ? Text.NormalizeKey(v) : null;
        if (vatId is not null && (vatId.Length is < 4 or > MaxVatIdLength || !vatId.All(char.IsAsciiLetterOrDigit)))
            return Error.Validation("pos.customer_vat_id", "The VAT id is not valid.");
        if (TooLong(d.Company, MaxNameLength) || TooLong(d.Phone, MaxPhoneLength) || TooLong(d.Street, MaxStreetLength)
            || TooLong(d.PostalCode, MaxPostalCodeLength) || TooLong(d.City, MaxCityLength))
            return Error.Validation("pos.customer_field_length", "A customer field is too long.");

        Name = name;
        Company = Clean(d.Company);
        Email = email;
        Phone = Clean(d.Phone);
        Street = Clean(d.Street);
        PostalCode = Clean(d.PostalCode);
        City = Clean(d.City);
        CountryCode = country;
        VatId = vatId;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>What a receipt prints about its customer (no email/phone).</summary>
    public DocumentCustomer ToDocument() => new(Id, Name, Company, Street, PostalCode, City, CountryCode, VatId);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TooLong(string? value, int max) => value?.Trim().Length > max;
}
