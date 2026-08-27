using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class IdentitySessionConfiguration : IEntityTypeConfiguration<IdentitySession>
{
    public void Configure(EntityTypeBuilder<IdentitySession> builder)
    {
        builder.ToTable("sessions", "identity", table =>
        {
            table.HasCheckConstraint("ck_sessions_id_uuid_v7", UuidV7Constraint.Sql);
            table.HasCheckConstraint(
                "ck_sessions_purpose",
                "purpose IN ('auth_sso', 'web')");
            table.HasCheckConstraint(
                "ck_sessions_validator_hash",
                "octet_length(validator_hash) = 32");
            table.HasCheckConstraint(
                "ck_sessions_security_version",
                "security_version > 0");
            table.HasCheckConstraint(
                "ck_sessions_timestamps",
                """
                authenticated_at <= created_at
                AND created_at <= last_seen_at
                AND last_seen_at <= updated_at
                AND created_at < sliding_expires_at
                AND sliding_expires_at <= absolute_expires_at
                AND (
                    revoked_at IS NULL
                    OR (
                        created_at <= revoked_at
                        AND revoked_at <= updated_at
                    )
                )
                """);
            table.HasCheckConstraint(
                "ck_sessions_auth_sso_shape",
                """
                purpose <> 'auth_sso'
                OR (
                    auth_sso_session_id IS NULL
                    AND web_origin IS NULL
                    AND capabilities = 0
                    AND sliding_expires_at = absolute_expires_at
                )
                """);
            table.HasCheckConstraint(
                "ck_sessions_web_shape",
                """
                purpose <> 'web'
                OR (
                    auth_sso_session_id IS NOT NULL
                    AND web_origin IS NOT NULL
                    AND web_origin IN (
                        'https://relisten.net',
                        'https://web.relisten.localhost:5173'
                    )
                    AND capabilities = 7
                )
                """);
        });

        builder.HasKey(session => session.Id);
        builder.HasAlternateKey(session => new { session.Id, session.UserId });
        builder.Property(session => session.Id)
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
        builder.Property(session => session.Purpose).HasMaxLength(16);
        builder.Property(session => session.ValidatorHash).HasColumnType("bytea");
        builder.Property(session => session.WebOrigin).HasMaxLength(128);
        builder.Property(session => session.Capabilities).HasConversion<int>();

        builder.HasIndex(session => new
        {
            session.UserId,
            session.RevokedAt,
            session.AbsoluteExpiresAt
        });
        builder.HasIndex(session => new
        {
            session.AuthSsoSessionId,
            session.RevokedAt
        });

        builder.HasOne(session => session.User)
            .WithMany(user => user.IdentitySessions)
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(session => session.AuthSsoSession)
            .WithMany(session => session.WebSessions)
            .HasForeignKey(session => new
            {
                session.AuthSsoSessionId,
                session.UserId
            })
            .HasPrincipalKey(session => new { session.Id, session.UserId })
            .OnDelete(DeleteBehavior.NoAction);
    }
}
