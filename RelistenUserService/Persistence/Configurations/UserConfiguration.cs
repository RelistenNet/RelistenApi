using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", "identity", table =>
        {
            table.HasCheckConstraint("ck_users_id_uuid_v7", UuidV7Constraint.Sql);
            table.HasCheckConstraint(
                "ck_users_status",
                "status IN ('active', 'disabled', 'deleting')");
            table.HasCheckConstraint(
                "ck_users_username",
                "username = lower(username) AND username ~ '^[a-z0-9_]{3,30}$'");
        });

        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id)
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
        builder.Property(user => user.Status).HasMaxLength(16);
        builder.Property(user => user.Username).HasMaxLength(30);
        builder.HasIndex(user => user.Username).IsUnique();
        builder.HasIndex(user => new { user.Status, user.UpdatedAt });
    }
}
