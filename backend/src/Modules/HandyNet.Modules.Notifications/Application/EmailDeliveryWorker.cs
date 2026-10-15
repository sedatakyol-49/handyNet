using HandyNet.Modules.Notifications.Domain;
using HandyNet.Modules.Notifications.Infrastructure;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Notifications.Application;

/// <summary>
/// Sends due emails. Rows are claimed with FOR UPDATE SKIP LOCKED so several API instances can run
/// the worker without sending an email twice.
/// </summary>
public sealed class EmailDeliveryWorker(IServiceScopeFactory scopeFactory, ILogger<EmailDeliveryWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DeliverBatchAsync(stoppingToken) == 0)
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email delivery loop failed");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    public async Task<int> DeliverBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;
        var due = await db.Set<EmailMessage>()
            .FromSql($"""
                SELECT * FROM notifications.email_messages
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var email in due)
        {
            try
            {
                await sender.SendAsync(email.Recipient, email.Subject, email.Body, ct);
                email.MarkSent(clock.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                email.MarkAttemptFailed(ex.Message, clock.UtcNow);
                logger.LogWarning(ex, "Email {EmailId} to {Recipient} failed (attempt {Attempt})", email.Id, email.Recipient, email.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return due.Count;
    }
}
