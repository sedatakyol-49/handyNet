using Microsoft.EntityFrameworkCore;

namespace HandyNet.Persistence;

/// <summary>
/// Contributed by each module to register its own entity configurations. A module only maps
/// tables in its own schema and never declares navigations into another module's entities.
/// </summary>
public interface IModuleModel
{
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>
/// Single unit of work shared by all modules in the monolith (see ADR-0002). One physical context
/// lets cross-module operations such as "reserve stock + create order" commit atomically.
/// </summary>
public sealed class HandyNetDbContext(DbContextOptions<HandyNetDbContext> options, IEnumerable<IModuleModel> modules)
    : DbContext(options)
{
    public const string InfraSchema = "infra";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());

        foreach (var module in modules)
            module.Configure(modelBuilder);
    }
}
