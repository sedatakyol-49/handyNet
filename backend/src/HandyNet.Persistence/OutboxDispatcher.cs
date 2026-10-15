using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HandyNet.Persistence;

/// <summary>Handles outbox messages of one event type. Implementations must be idempotent (at-least-once delivery).</summary>
public interface IOutboxHandler
{
    string EventType { get; }
    /// <param name="messageId">Outbox message id — use it as an idempotency key for side effects.</param>
    Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken);
}

/// <summary>
/// Polls the outbox and dispatches messages to handlers. Uses FOR UPDATE SKIP LOCKED so several
/// API instances can run the dispatcher concurrently without double-processing a batch.
/// </summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await DispatchBatchAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch failed");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToLookup(h => h.EventType);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var messages = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM infra.outbox_messages
                WHERE processed_at IS NULL AND attempts < {MaxAttempts}
                ORDER BY occurred_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                foreach (var handler in handlers[message.Type])
                    await handler.HandleAsync(message.Id, message.Payload, ct);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message;
                logger.LogWarning(ex, "Outbox message {MessageId} ({Type}) failed, attempt {Attempt}",
                    message.Id, message.Type, message.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return messages.Count;
    }
}
