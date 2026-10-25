namespace HandyNet.Modules.Notifications.Domain;

public enum EmailStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>
/// An email queued for delivery. Created by outbox handlers; (SourceId, Recipient) is unique, so
/// re-processing the same outbox message never sends the same email twice. Delivery retries with
/// exponential backoff and gives up after <see cref="MaxAttempts"/> (status Failed, kept for support).
/// </summary>
public sealed class EmailMessage
{
    public const int MaxAttempts = 8;

    private EmailMessage() { }

    public Guid Id { get; private set; }
    public Guid SourceId { get; private set; }
    public string Template { get; private set; } = null!;
    public string Recipient { get; private set; } = null!;
    public Guid? RecipientUserId { get; private set; }
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;

    /// <summary>Optional HTML alternative of <see cref="Body"/> (e-receipts).</summary>
    public string? HtmlBody { get; private set; }

    /// <summary>Optional attachment (quotation/invoice PDF); the content is dropped once the email was delivered or gave up.</summary>
    public string? AttachmentName { get; private set; }
    public string? AttachmentContentType { get; private set; }
    public byte[]? AttachmentContent { get; private set; }

    public EmailAttachment? Attachment => AttachmentName is not null && AttachmentContent is not null
        ? new EmailAttachment(AttachmentName, AttachmentContentType ?? "application/octet-stream", AttachmentContent)
        : null;
    public EmailStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public static EmailMessage Queue(Guid sourceId, string template, string recipient, Guid? recipientUserId,
        RenderedEmail content, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        SourceId = sourceId,
        Template = template,
        Recipient = recipient.Trim().ToLowerInvariant(),
        RecipientUserId = recipientUserId,
        Subject = content.Subject,
        Body = content.Body,
        HtmlBody = content.Html,
        AttachmentName = content.Attachment?.FileName,
        AttachmentContentType = content.Attachment?.ContentType,
        AttachmentContent = content.Attachment?.Content,
        Status = EmailStatus.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    /// <summary>Replaces the body of account emails once delivery is finished.</summary>
    public const string RedactedBody = "[redacted after delivery: contained a one-time link]";

    /// <summary>
    /// Account emails ("auth.*") carry a single-use link — the only plaintext copy of its token. The body is
    /// dropped once the email left the queue, so the email table does not keep usable secrets.
    /// </summary>
    public bool ContainsSecret => Template.StartsWith("auth.", StringComparison.Ordinal);

    public void MarkSent(DateTimeOffset now)
    {
        Status = EmailStatus.Sent;
        SentAt = now;
        Attempts++;
        LastError = null;
        RedactIfSecret();
        AttachmentContent = null;
    }

    public void MarkAttemptFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
        if (Attempts >= MaxAttempts)
        {
            Status = EmailStatus.Failed;
            RedactIfSecret();
        AttachmentContent = null;
            return;
        }

        NextAttemptAt = now + BackoffFor(Attempts);
    }

    private void RedactIfSecret()
    {
        if (ContainsSecret)
        {
            Body = RedactedBody;
            HtmlBody = null;
        }
    }

    /// <summary>1, 2, 4, 8 … minutes, capped at 6 hours.</summary>
    public static TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts - 1), 360));
}

/// <summary>Html: optional HTML alternative (sent as multipart/alternative with the plain text).</summary>
public sealed record RenderedEmail(string Subject, string Body, string? Html = null, EmailAttachment? Attachment = null);

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);
