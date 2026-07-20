using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.ToTable("external_identities", "identity", table =>
            table.HasCheckConstraint("ck_external_identities_id_uuid_v7", UuidV7Constraint.Sql));
        builder.HasKey(identity => identity.Id);
        builder.Property(identity => identity.Id)
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
        builder.Property(identity => identity.Issuer).HasMaxLength(512);
        builder.Property(identity => identity.ProviderSubject).HasMaxLength(512);
        builder.Property(identity => identity.EmailAtProvider).HasMaxLength(320);
        builder.HasIndex(identity => new { identity.Issuer, identity.ProviderSubject })
            .IsUnique();
        builder.HasIndex(identity => new { identity.UserId, identity.Issuer })
            .IsUnique();
        builder.HasOne(identity => identity.User)
            .WithMany(user => user.ExternalIdentities)
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
