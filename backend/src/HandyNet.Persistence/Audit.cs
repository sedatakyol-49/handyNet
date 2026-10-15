using System.Text.Json;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HandyNet.Persistence;

/// <summary>Append-only record of changes to auditable entities. UPDATE/DELETE are revoked from the app role.</summary>
public sealed class AuditEntry
{
    public long Id { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? UserId { get; init; }
    public Guid? OrganizationId { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Action { get; init; }
    public required string Changes { get; init; }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("audit_log", HandyNetDbContext.InfraSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.EntityType).HasMaxLength(200);
        b.Property(x => x.EntityId).HasMaxLength(100);
        b.Property(x => x.Action).HasMaxLength(20);
        b.Property(x => x.Changes).HasColumnType("jsonb");
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        b.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
    }
}

public sealed class AuditInterceptor(IClock clock, ICurrentUser currentUser, ITenantContext tenant) : SaveChangesInterceptor
{
    private const int MaxValueLength = 2000;

    /// <summary>Written instead of values of <see cref="PersonalDataAttribute"/> properties.</summary>
    public const string Redacted = "[personal data]";

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
            Write(context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
            Write(context);
        return base.SavingChanges(eventData, result);
    }

    private void Write(DbContext context)
    {
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is IAuditable && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            var changes = Describe(entry);
            if (entry.State == EntityState.Modified && changes.Count == 0)
                continue;

            context.Set<AuditEntry>().Add(new AuditEntry
            {
                OccurredAt = clock.UtcNow,
                UserId = currentUser.IsAuthenticated ? currentUser.UserId : null,
                OrganizationId = tenant.OrganizationId,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = string.Join(",", entry.Metadata.FindPrimaryKey()!.Properties
                    .Select(p => entry.Property(p.Name).CurrentValue?.ToString())),
                Action = entry.State.ToString(),
                Changes = JsonSerializer.Serialize(changes),
            });
        }
    }

    private static Dictionary<string, object?> Describe(EntityEntry entry)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsConcurrencyToken || property.Metadata.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
                continue;
            var personal = property.Metadata.PropertyInfo?.IsDefined(typeof(PersonalDataAttribute), inherit: true) == true;

            switch (entry.State)
            {
                case EntityState.Added:
                    result[property.Metadata.Name] = personal ? Redacted : Trim(property.CurrentValue);
                    break;
                case EntityState.Deleted:
                    result[property.Metadata.Name] = personal ? Redacted : Trim(property.OriginalValue);
                    break;
                case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                    result[property.Metadata.Name] = personal ? Redacted : new { from = Trim(property.OriginalValue), to = Trim(property.CurrentValue) };
                    break;
            }
        }
        return result;
    }

    private static object? Trim(object? value) =>
        value is string s && s.Length > MaxValueLength ? s[..MaxValueLength] + "…" : value;
}
