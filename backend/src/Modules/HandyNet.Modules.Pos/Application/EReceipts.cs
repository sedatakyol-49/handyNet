using System.Text.Json;
using FluentValidation;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Email: an address, or null for the email of the receipt's customer.</summary>
/// <param name="Language">Language of the email (de, en, tr, es, ru, ar, fa); null = the email language of the company.</param>
public sealed record EmailReceiptRequest(string? Email, string? Language = null);

/// <summary>Recipient masked (e.g. "a***@example.com"): the response must not echo the full address on shared screens.</summary>
public sealed record ReceiptEmailDto(Guid Id, Guid ReceiptId, string RecipientMasked, DateTimeOffset RequestedAt);

/// <summary>Path relative to the web app (e.g. "/beleg/{token}"); the token is returned only here.</summary>
public sealed record ReceiptLinkDto(string Token, string Path, DateTimeOffset ExpiresAt);

/// <summary>A receipt as the public (QR) view shows it: the issued document without the customer block.</summary>
/// <param name="Branding">Logo and receipt texts of the issuing company as currently configured (ADR-0014).</param>
public sealed record PublicReceiptDto(ReceiptDocument Document, bool Cancelled, DateTimeOffset ExpiresAt, ReceiptBranding? Branding = null);

internal sealed class EmailReceiptValidator : AbstractValidator<EmailReceiptRequest>
{
    public EmailReceiptValidator()
    {
        RuleFor(x => x.Email).MaximumLength(ReceiptEmail.MaxEmailLength);
        RuleFor(x => x.Language).Must(ReceiptEmail.IsEmailLanguage).WithErrorCode("validation.language").WithMessage("The email language is not supported.");
    }
}

internal sealed class PublicReceiptRow
{
    public string Document { get; set; } = null!;
    public bool Cancelled { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid OrganizationId { get; set; }
}

/// <summary>E-receipts: email via the Notifications outbox, and public read-only links (QR) with expiry.</summary>
internal sealed class EReceiptService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IOptions<PosOptions> options, IClock clock,
    IOrganizationBranding branding)
{
    public const string PublicPathPrefix = "/beleg/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    public async Task<Result<ReceiptEmailDto>> EmailAsync(Guid receiptId, EmailReceiptRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.Set<Receipt>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId && r.OrganizationId == OrganizationId, ct);
        if (receipt is null)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        var email = request.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = receipt.CustomerId is { } customerId
                ? await db.Set<Customer>().AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Email).FirstOrDefaultAsync(ct)
                : null;
            if (string.IsNullOrWhiteSpace(email))
                return Error.Validation("pos.receipt_email_missing", "Enter an email address (the receipt has no customer with an email).");
        }
        // Serialize requests per receipt so the limit holds under concurrency.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"receipt-email:" + receiptId}, 0))", ct);
        var sent = await db.Set<ReceiptEmail>().CountAsync(e => e.ReceiptId == receiptId, ct);
        var created = ReceiptEmail.Request(receipt, email, sent, ReceiptEmail.ContentOf(receipt.Document!), actor.UserId, clock.UtcNow, request.Language);
        if (!created.IsSuccess)
            return created.Error!;
        db.Add(created.Value);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReceiptEmailDto(created.Value.Id, receiptId, Mask(created.Value.Recipient), created.Value.RequestedAt);
    }

    public async Task<Result<ReceiptLinkDto>> CreateLinkAsync(Guid receiptId, CancellationToken ct)
    {
        var receipt = await db.Set<Receipt>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId && r.OrganizationId == OrganizationId, ct);
        if (receipt is null)
            return Error.NotFound("pos.receipt_not_found", "Receipt not found.");
        var token = OpaqueToken.New();
        var link = ReceiptLink.Create(receipt, OpaqueToken.HashOf(token), actor.UserId, clock.UtcNow, TimeSpan.FromDays(options.Value.PublicReceiptLinkDays));
        db.Add(link);
        await db.SaveChangesAsync(ct);
        return new ReceiptLinkDto(token, PublicPathPrefix + token, link.ExpiresAt);
    }

    /// <summary>
    /// Anonymous read through a SECURITY DEFINER function that finds exactly one receipt by the token hash (tenant tables
    /// stay under RLS; no tenant context exists here). Unknown and expired tokens look the same (404).
    /// </summary>
    public async Task<Result<PublicReceiptDto>> GetPublicAsync(string token, CancellationToken ct)
    {
        var notFound = Error.NotFound("pos.receipt_link_not_found", "This receipt link does not exist or has expired.");
        if (!OpaqueToken.LooksValid(token))
            return notFound;
        var hash = OpaqueToken.HashOf(token);
        var rows = await db.Database.SqlQuery<PublicReceiptRow>($"SELECT document, cancelled, expires_at, organization_id FROM pos.public_receipt({hash})").ToListAsync(ct);
        if (rows.FirstOrDefault() is not { } row)
            return notFound;
        var document = JsonSerializer.Deserialize<ReceiptDocument>(row.Document, Json)!;
        return new PublicReceiptDto(document with { Customer = null }, row.Cancelled, row.ExpiresAt,
            await branding.GetReceiptBrandingAsync(row.OrganizationId, ct));
    }

    internal static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "***" + email[Math.Max(at, 0)..] : email[0] + "***" + email[at..];
    }
}
