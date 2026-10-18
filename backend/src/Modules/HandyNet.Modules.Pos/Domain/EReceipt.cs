using System.Net.Mail;
using HandyNet.Modules.Pos.Contracts;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// A request to email a receipt (e-receipt). Append-only record of who sent which receipt where; raising
/// <see cref="ReceiptEmailRequested"/> hands it to the Notifications module through the outbox (same transaction). The
/// address is personal data and never copied into the audit log.
/// </summary>
public sealed class ReceiptEmail : Entity, IAuditable
{
    public const int MaxPerReceipt = 5;
    public const int MaxEmailLength = 320;

    private ReceiptEmail() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ReceiptId { get; private set; }

    [PersonalData]
    public string Recipient { get; private set; } = null!;
    public DateTimeOffset RequestedAt { get; private set; }
    public Guid RequestedBy { get; private set; }

    public static Result<string> NormalizeAddress(string? email)
    {
        var e = email?.Trim();
        if (string.IsNullOrEmpty(e) || e.Length > MaxEmailLength || !MailAddress.TryCreate(e, out var parsed) || parsed.Address != e)
            return Error.Validation("pos.receipt_email_invalid", "The email address is not valid.");
        return e.ToLowerInvariant();
    }

    public static Result<ReceiptEmail> Request(Receipt receipt, string? email, int alreadySent, EReceiptContent content, Guid userId, DateTimeOffset now)
    {
        var address = NormalizeAddress(email);
        if (!address.IsSuccess)
            return address.Error!;
        if (alreadySent >= MaxPerReceipt)
            return Error.Conflict("pos.receipt_email_limit", $"This receipt was already emailed {MaxPerReceipt} times.");
        var request = new ReceiptEmail
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = receipt.OrganizationId,
            ReceiptId = receipt.Id,
            Recipient = address.Value,
            RequestedAt = now,
            RequestedBy = userId,
        };
        request.Raise(new ReceiptEmailRequested(request.Id, receipt.OrganizationId, receipt.Id, address.Value, content));
        return request;
    }

    /// <summary>The receipt as printed, for the email (no customer block, no §25a internals — the document never has them).</summary>
    public static EReceiptContent ContentOf(ReceiptDocument d) => new(
        d.Title,
        d.Seller.Name,
        d.Seller.LegalName,
        [d.Seller.Street, $"{d.Seller.PostalCode} {d.Seller.City}"],
        d.Seller.VatId,
        d.RegisterName,
        d.ReceiptNumber,
        d.IssuedAt,
        d.Currency,
        d.Lines.Select(l => new EReceiptLine(l.Description, l.Imei, l.Quantity, l.UnitPriceMinor, l.DiscountMinor, l.LineGrossMinor, l.TaxLabel, l.Note))
            .ToList(),
        d.Vat.Select(v => new EReceiptVat(v.Label, v.NetMinor, v.VatMinor, v.GrossMinor)).ToList(),
        d.TotalGrossMinor,
        d.Payments.Select(p => new EReceiptPayment(p.Method, p.AmountMinor, p.TenderedMinor)).ToList(),
        d.ChangeMinor,
        d.Notes);
}

/// <summary>
/// A public, unguessable, read-only link to one receipt (QR code on the receipt screen). Only the SHA-256 of the token is
/// stored; the link expires. The public view shows the receipt itself, never the customer snapshot.
/// </summary>
public sealed class ReceiptLink : IAuditable
{
    private ReceiptLink() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    public static ReceiptLink Create(Receipt receipt, string tokenHash, Guid userId, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = receipt.OrganizationId,
        ReceiptId = receipt.Id,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
        CreatedBy = userId,
    };

    public bool IsValid(DateTimeOffset now) => ExpiresAt > now;
}
