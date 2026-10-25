using HandyNet.SharedKernel.Documents;

namespace HandyNet.Modules.Tenancy.Contracts;

/// <summary>
/// How an organization presents itself on its receipts (ADR-0014): logo (public URL of the small PNG), receipt header and
/// footer texts. Safe for anonymous pages (public e-receipt): contains nothing that is not printed on a receipt anyway.
/// </summary>
/// <param name="LogoUrl">Relative URL of the screen/receipt logo (PNG), e.g. <c>/api/logos/{id}/screen</c>; null without a logo.</param>
public sealed record ReceiptBranding(string? LogoUrl, string? ReceiptHeader, string? ReceiptFooter)
{
    public static readonly ReceiptBranding None = new(null, null, null);
}

/// <summary>Letterhead data for documents an organization issues (logo JPEG + footer lines) and its default payment terms.</summary>
public sealed record DocumentBranding(DocumentLetterhead Letterhead, int? PaymentTermsDays);

/// <summary>Public read API of the Tenancy module: the company's branding for receipts and PDF documents.</summary>
public interface IOrganizationBranding
{
    /// <summary>Logo URL and receipt texts; <see cref="ReceiptBranding.None"/> for an unknown organization.</summary>
    Task<ReceiptBranding> GetReceiptBrandingAsync(Guid organizationId, CancellationToken ct);

    /// <summary>
    /// Logo as JPEG (read from file storage) and letterhead footer lines (address/contact, managing director/register/tax
    /// ids, bank details). A missing logo file degrades to a document without logo (logged), never to an error.
    /// </summary>
    Task<DocumentBranding> GetDocumentBrandingAsync(Guid organizationId, CancellationToken ct);
}
