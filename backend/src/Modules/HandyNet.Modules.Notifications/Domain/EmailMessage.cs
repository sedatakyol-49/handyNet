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
        Status = EmailStatus.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    public void MarkSent(DateTimeOffset now)
    {
        Status = EmailStatus.Sent;
        SentAt = now;
        Attempts++;
        LastError = null;
    }

    public void MarkAttemptFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
        if (Attempts >= MaxAttempts)
        {
            Status = EmailStatus.Failed;
            return;
        }

        NextAttemptAt = now + BackoffFor(Attempts);
    }

    /// <summary>1, 2, 4, 8 … minutes, capped at 6 hours.</summary>
    public static TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts - 1), 360));
}

public sealed record RenderedEmail(string Subject, string Body);
