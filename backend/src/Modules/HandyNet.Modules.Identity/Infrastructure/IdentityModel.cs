using HandyNet.Modules.Identity.Domain;
using HandyNet.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Identity.Infrastructure;

public sealed class IdentityModel : IModuleModel
{
    public const string Schema = "identity";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.Email).IsUnique();
            b.Ignore(x => x.DomainEvents);
        });
    }
}
