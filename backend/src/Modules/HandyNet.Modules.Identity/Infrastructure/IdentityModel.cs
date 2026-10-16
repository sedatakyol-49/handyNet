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
            b.Ignore(x => x.EmailVerified);
            b.Ignore(x => x.DomainEvents);
        });

        // Not tenant-owned (a user spans organizations): no RLS, protected by application authorization.
        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.ToTable("refresh_tokens", Schema, t =>
            {
                t.HasCheckConstraint("ck_refresh_tokens_lifetime", "expires_at > created_at");
                t.HasCheckConstraint("ck_refresh_tokens_rotation", "(used_at IS NULL) = (replaced_by_id IS NULL)");
                t.HasCheckConstraint("ck_refresh_tokens_revocation", "(revoked_at IS NULL) = (revocation_reason IS NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.RevocationReason).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.TokenHash).IsUnique();
            // A token is rotated at most once (DB-enforced next to the optimistic concurrency check).
            b.HasIndex(x => x.ReplacedById).IsUnique();
            b.HasIndex(x => x.FamilyId);
            b.HasIndex(x => x.UserId).HasFilter("revoked_at IS NULL AND used_at IS NULL").HasDatabaseName("ix_refresh_tokens_active_by_user");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountToken>(b =>
        {
            b.ToTable("account_tokens", Schema, t =>
            {
                t.HasCheckConstraint("ck_account_tokens_lifetime", "expires_at > created_at");
                t.HasCheckConstraint("ck_account_tokens_single_outcome", "used_at IS NULL OR revoked_at IS NULL");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.UserId, x.Purpose }).HasFilter("used_at IS NULL AND revoked_at IS NULL").HasDatabaseName("ix_account_tokens_open_by_user");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
