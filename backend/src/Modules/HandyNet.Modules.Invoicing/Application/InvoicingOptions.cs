using HandyNet.Modules.Invoicing.Domain;

namespace HandyNet.Modules.Invoicing.Application;

public sealed class InvoicingOptions
{
    public const string Section = "Invoicing";

    /// <summary>The platform company that issues commission invoices. appsettings.json ships placeholders (refused in Production).</summary>
    public IssuerOptions Issuer { get; set; } = new();

    /// <summary>VAT rate on the commission; must match <c>Payments:CommissionVatBasisPoints</c> (each line is checked against it).</summary>
    public int VatRateBasisPoints { get; set; } = 1900;

    /// <summary>Background job: issue last month's invoices and credit notes automatically (idempotent).</summary>
    public bool AutoIssue { get; set; } = true;

    /// <summary>How often the job checks whether last month still needs issuing.</summary>
    public int AutoIssueCheckMinutes { get; set; } = 60;
}

public sealed class IssuerOptions
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string Street { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "DE";
    public string? VatId { get; set; }
    public string? TaxNumber { get; set; }

    public InvoiceParty ToParty() => new(Name.Trim(), LegalName?.Trim(), Street.Trim(), PostalCode.Trim(), City.Trim(), CountryCode.Trim(),
        string.IsNullOrWhiteSpace(VatId) ? null : VatId.Trim(), string.IsNullOrWhiteSpace(TaxNumber) ? null : TaxNumber.Trim());
}
