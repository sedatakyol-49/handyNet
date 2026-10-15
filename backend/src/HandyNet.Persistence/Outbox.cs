using System.Text.Json;
using HandyNet.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HandyNet.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", HandyNetDbContext.InfraSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(300).IsRequired();
        b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.LastError).HasMaxLength(4000);
        b.HasIndex(x => x.OccurredAt).HasFilter("processed_at IS NULL").HasDatabaseName("ix_outbox_messages_unprocessed");
    }
}

/// <summary>
/// Converts domain events raised by tracked entities into outbox rows inside the same SaveChanges,
/// so an event is persisted if and only if the state change that produced it is committed.
/// </summary>
public sealed class OutboxInterceptor(IClock clock) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
            Enqueue(context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
            Enqueue(context);
        return base.SavingChanges(eventData, result);
    }

    private void Enqueue(DbContext context)
    {
        var events = context.ChangeTracker.Entries<Entity>()
            .SelectMany(e => e.Entity.PopDomainEvents())
            .ToList();

        foreach (var domainEvent in events)
        {
            context.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                Type = domainEvent.GetType().FullName!,
                Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                OccurredAt = clock.UtcNow,
            });
        }
    }
}
