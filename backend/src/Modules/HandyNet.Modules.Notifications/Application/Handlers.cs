using System.Text.Json;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Notifications.Domain;
using HandyNet.Modules.Notifications.Infrastructure;
using HandyNet.Modules.Ordering.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Notifications.Application;

/// <summary>Resolves recipients and queues emails idempotently per outbox message.</summary>
internal sealed class EmailQueue(
    HandyNetDbContext db,
    IOrganizationDirectory organizations,
    IUserAccounts users,
    IClock clock)
{
    private static readonly MembershipRole[] Responsible = [MembershipRole.Owner, MembershipRole.Manager];

    public async Task<IReadOnlyList<UserSummary>> ResponsiblePeopleAsync(Guid organizationId, CancellationToken ct)
    {
        var ids = await organizations.GetMemberUserIdsAsync(organizationId, Responsible, ct);
        return (await users.GetAsync(ids, ct)).Values.ToList();
    }

    public async Task<string> OrganizationNameAsync(Guid organizationId, CancellationToken ct) =>
        (await organizations.GetAsync([organizationId], ct)).GetValueOrDefault(organizationId)?.Name ?? "?";

    public Task<bool> AnyQueuedAsync(Guid sourceId, CancellationToken ct) =>
        db.Set<EmailMessage>().AsNoTracking().AnyAsync(e => e.SourceId == sourceId, ct);

    /// <summary>Adds emails for recipients that do not have one for this source yet (at-least-once safe).</summary>
    public async Task QueueAsync(Guid sourceId, string template, IReadOnlyList<UserSummary> recipients, RenderedEmail content, CancellationToken ct)
    {
        var alreadyQueued = await db.Set<EmailMessage>().AsNoTracking()
            .Where(e => e.SourceId == sourceId)
            .Select(e => e.Recipient)
            .ToListAsync(ct);

        foreach (var recipient in recipients.DistinctBy(r => r.Email))
        {
            if (alreadyQueued.Contains(recipient.Email.ToLowerInvariant()))
                continue;
            db.Add(EmailMessage.Queue(sourceId, template, recipient.Email, recipient.Id, content, clock.UtcNow));
        }
    }
}

/// <summary>E-receipt requested at the till: one email to the given address per request (idempotent per outbox message).</summary>
internal sealed class ReceiptEmailRequestedHandler(HandyNetDbContext db, IClock clock) : IOutboxHandler
{
    public string EventType => typeof(HandyNet.Modules.Pos.Contracts.ReceiptEmailRequested).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        if (await db.Set<EmailMessage>().AsNoTracking().AnyAsync(e => e.SourceId == messageId, ct))
            return;
        var e = JsonSerializer.Deserialize<HandyNet.Modules.Pos.Contracts.ReceiptEmailRequested>(payload)!;
        db.Add(EmailMessage.Queue(messageId, EReceiptTemplate.Template, e.Recipient, null, EReceiptTemplate.Render(e.Content), clock.UtcNow));
    }
}

internal sealed class OrderPlacedHandler(EmailQueue queue, IOptions<NotificationOptions> options) : IOutboxHandler
{
    public string EventType => typeof(OrderPlaced).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<OrderPlaced>(payload)!;
        var seller = await queue.OrganizationNameAsync(e.SellerOrganizationId, ct);
        var buyer = await queue.OrganizationNameAsync(e.BuyerOrganizationId, ct);
        var recipients = await queue.ResponsiblePeopleAsync(e.SellerOrganizationId, ct);
        var link = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/app/bestellungen/{e.OrderId}";
        await queue.QueueAsync(messageId, "order.placed", recipients,
            EmailTemplates.OrderPlacedForSeller(seller, buyer, e.GrossTotalMinor, link), ct);
    }
}

internal sealed class OrderStatusChangedHandler(EmailQueue queue, IOptions<NotificationOptions> options) : IOutboxHandler
{
    public string EventType => typeof(OrderStatusChanged).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<OrderStatusChanged>(payload)!;
        if (e.BuyerOrganizationId == Guid.Empty || e.SellerOrganizationId == Guid.Empty)
            return; // event written before parties were part of the payload

        // The party that did NOT act is informed: buyer cancels → seller; seller acts → buyer.
        var (recipientOrg, actorOrg) = e.To == nameof(OrderStatus.Cancelled)
            ? (e.SellerOrganizationId, e.BuyerOrganizationId)
            : (e.BuyerOrganizationId, e.SellerOrganizationId);

        var actor = await queue.OrganizationNameAsync(actorOrg, ct);
        var recipients = await queue.ResponsiblePeopleAsync(recipientOrg, ct);
        var link = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/app/bestellungen/{e.OrderId}";
        await queue.QueueAsync(messageId, $"order.{e.To.ToLowerInvariant()}", recipients,
            EmailTemplates.OrderStatusForCounterparty(e.To, actor, e.Reason, link), ct);
    }
}

/// <summary>Web app routes that consume the account links. The token travels in the URL fragment, so it never
/// reaches server or proxy access logs, nor Referer headers.</summary>
public static class AccountLinks
{
    public const string VerifyEmailPath = "/email-bestaetigen";
    public const string ResetPasswordPath = "/passwort-zuruecksetzen";

    public static string Build(string publicBaseUrl, string path, string token) =>
        $"{publicBaseUrl.TrimEnd('/')}{path}#token={Uri.EscapeDataString(token)}";
}

/// <summary>
/// Issues the verification token here (via Identity's contract) instead of carrying it in the event, so the outbox
/// never stores it. Token row and email row are committed together by the dispatcher.
/// </summary>
internal sealed class EmailVerificationRequestedHandler(EmailQueue queue, IUserAccounts users, IOptions<NotificationOptions> options) : IOutboxHandler
{
    public string EventType => typeof(EmailVerificationRequested).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        if (await queue.AnyQueuedAsync(messageId, ct))
            return; // re-delivery: the email (and its token) already exist
        var e = JsonSerializer.Deserialize<EmailVerificationRequested>(payload)!;
        if (await users.IssueEmailVerificationTokenAsync(e.UserId, ct) is not { } issued)
            return; // verified meanwhile, or deactivated
        var link = AccountLinks.Build(options.Value.PublicBaseUrl, AccountLinks.VerifyEmailPath, issued.Token);
        await queue.QueueAsync(messageId, EmailTemplates.EmailVerificationTemplate, [issued.User],
            EmailTemplates.EmailVerification(issued.User.DisplayName, link, issued.ValidFor, e.Language), ct);
    }
}

/// <summary>Same pattern as <see cref="EmailVerificationRequestedHandler"/>: the reset token is issued here, not in the event.</summary>
internal sealed class PasswordResetRequestedHandler(EmailQueue queue, IUserAccounts users, IOptions<NotificationOptions> options) : IOutboxHandler
{
    public string EventType => typeof(PasswordResetRequested).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        if (await queue.AnyQueuedAsync(messageId, ct))
            return;
        var e = JsonSerializer.Deserialize<PasswordResetRequested>(payload)!;
        if (await users.IssuePasswordResetTokenAsync(e.UserId, ct) is not { } issued)
            return;
        var link = AccountLinks.Build(options.Value.PublicBaseUrl, AccountLinks.ResetPasswordPath, issued.Token);
        await queue.QueueAsync(messageId, EmailTemplates.PasswordResetTemplate, [issued.User],
            EmailTemplates.PasswordReset(issued.User.DisplayName, link, issued.ValidFor, e.Language), ct);
    }
}

internal sealed class OrganizationVerifiedHandler(EmailQueue queue, IOptions<NotificationOptions> options) : IOutboxHandler
{
    public string EventType => typeof(OrganizationVerified).FullName!;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<OrganizationVerified>(payload)!;
        var name = await queue.OrganizationNameAsync(e.OrganizationId, ct);
        var recipients = await queue.ResponsiblePeopleAsync(e.OrganizationId, ct);
        await queue.QueueAsync(messageId, "organization.verified", recipients,
            EmailTemplates.OrganizationVerified(name, options.Value.PublicBaseUrl.TrimEnd('/') + "/app"), ct);
    }
}
