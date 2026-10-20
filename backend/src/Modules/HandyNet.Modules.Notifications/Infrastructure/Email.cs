using HandyNet.Modules.Notifications.Domain;
using HandyNet.Persistence;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace HandyNet.Modules.Notifications.Infrastructure;

public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    /// <summary>Base URL of the web app, used for links in emails.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:4200";

    public string FromAddress { get; set; } = "no-reply@handynet.local";
    public string FromName { get; set; } = "SEHA";

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    /// <summary>Empty = emails are only written to the log (development).</summary>
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Auto (default) negotiates TLS; use None only for a local test server such as Mailpit.</summary>
    public string Security { get; set; } = "Auto";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);

    /// <summary>With an optional HTML alternative; senders without HTML support send the plain text.</summary>
    Task SendAsync(string to, string subject, string body, string? htmlBody, CancellationToken ct) => SendAsync(to, subject, body, ct);
}

internal sealed class SmtpEmailSender(IOptions<NotificationOptions> options) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct) => SendAsync(to, subject, body, null, ct);

    public async Task SendAsync(string to, string subject, string body, string? htmlBody, CancellationToken ct)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = htmlBody is null
            ? new TextPart("plain") { Text = body }
            : new BodyBuilder { TextBody = body, HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        var security = Enum.TryParse<SecureSocketOptions>(o.Smtp.Security, true, out var parsed) ? parsed : SecureSocketOptions.Auto;
        await client.ConnectAsync(o.Smtp.Host, o.Smtp.Port, security, ct);
        if (!string.IsNullOrEmpty(o.Smtp.Username))
            await client.AuthenticateAsync(o.Smtp.Username, o.Smtp.Password ?? string.Empty, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Development fallback when no SMTP host is configured.</summary>
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        logger.LogInformation("Email (not sent, no SMTP configured) to {To}: {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}

public sealed class NotificationsModel : IModuleModel
{
    public const string Schema = "notifications";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailMessage>(b =>
        {
            b.ToTable("email_messages", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Template).HasMaxLength(100).IsRequired();
            b.Property(x => x.Recipient).HasMaxLength(320).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            b.Property(x => x.Body).IsRequired();
            b.Property(x => x.HtmlBody);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasIndex(x => new { x.SourceId, x.Recipient }).IsUnique();
            b.HasIndex(x => x.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_email_messages_due");
        });
    }
}
