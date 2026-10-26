using HandyNet.Modules.Pos.Contracts;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>Stored as text.</summary>
public enum SalesDocumentType
{
    Quotation,
    Invoice,
}

/// <summary>
/// A request to email a quotation or invoice (PDF attachment) to the customer. Append-only; raising
/// <see cref="SalesDocumentEmailRequested"/> hands it to Notifications through the outbox in the same transaction. The address
/// is personal data and never copied into the audit log.
/// </summary>
public sealed class SalesDocumentEmail : Entity, IAuditable
{
    public const int MaxPerDocument = 10;

    private SalesDocumentEmail() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public SalesDocumentType DocumentType { get; private set; }
    public Guid DocumentId { get; private set; }

    [PersonalData]
    public string Recipient { get; private set; } = null!;
    public DateTimeOffset RequestedAt { get; private set; }
    public Guid RequestedBy { get; private set; }

    public static Result<SalesDocumentEmail> Request(Guid organizationId, SalesDocumentType type, Guid documentId, SalesDocument document, string? email,
        int alreadySent, byte[] pdf, Guid userId, DateTimeOffset now, string? language = null)
    {
        if (!ReceiptEmail.IsEmailLanguage(language))
            return Error.Validation("pos.email_language_invalid", "The email language is not supported.");
        var address = ReceiptEmail.NormalizeAddress(email);
        if (!address.IsSuccess)
            return Error.Validation("pos.sales_email_invalid", "The email address is not valid.");
        if (alreadySent >= MaxPerDocument)
            return Error.Conflict("pos.sales_email_limit", $"This document was already emailed {MaxPerDocument} times.");
        var request = new SalesDocumentEmail
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            DocumentType = type,
            DocumentId = documentId,
            Recipient = address.Value,
            RequestedAt = now,
            RequestedBy = userId,
        };
        request.Raise(new SalesDocumentEmailRequested(request.Id, organizationId, documentId, document.Kind, document.Title, document.Number,
            address.Value, document.Seller.Name, document.TotalGrossMinor, document.Date, document.DueDate, document.ValidUntil, $"{document.Number}.pdf",
            Convert.ToBase64String(pdf), ReceiptEmail.NormalizeLanguage(language)));
        return request;
    }
}
