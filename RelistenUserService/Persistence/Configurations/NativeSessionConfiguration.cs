using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class NativeSessionConfiguration : IEntityTypeConfiguration<NativeSession>
{
    public void Configure(EntityTypeBuilder<NativeSession> builder)
    {
        builder.ToTable("native_sessions", "identity", table =>
            table.HasCheckConstraint("ck_native_sessions_id_uuid_v7", UuidV7Constraint.Sql));
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id)
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
        builder.Property(session => session.ClientId).HasMaxLength(100);
        builder.Property(session => session.DeviceName).HasMaxLength(120);
        builder.Property(session => session.Platform).HasMaxLength(32);
        builder.HasIndex(session => session.AuthorizationId).IsUnique();
        builder.HasIndex(session => new { session.UserId, session.RevokedAt, session.AbsoluteExpiresAt });
        builder.HasOne(session => session.User)
            .WithMany(user => user.NativeSessions)
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OpenIddictEntityFrameworkCoreAuthorization<Guid>>()
            .WithOne()
            .HasForeignKey<NativeSession>(session => session.AuthorizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
