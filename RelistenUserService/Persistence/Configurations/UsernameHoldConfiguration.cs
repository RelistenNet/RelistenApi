using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class UsernameHoldConfiguration : IEntityTypeConfiguration<UsernameHold>
{
    public void Configure(EntityTypeBuilder<UsernameHold> builder)
    {
        builder.ToTable("username_holds", "identity", table =>
            table.HasCheckConstraint("ck_username_holds_id_uuid_v7", UuidV7Constraint.Sql));
        builder.HasKey(hold => hold.Id);
        builder.Property(hold => hold.Id)
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
        builder.Property(hold => hold.Username).HasMaxLength(30);
        builder.HasIndex(hold => hold.Username).IsUnique();
        builder.HasIndex(hold => hold.ReleaseAt);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(hold => hold.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
