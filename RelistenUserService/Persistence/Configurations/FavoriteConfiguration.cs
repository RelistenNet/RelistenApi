using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class FavoriteConfiguration : IEntityTypeConfiguration<Favorite>
{
    public void Configure(EntityTypeBuilder<Favorite> builder)
    {
        builder.ToTable("favorites", "user_data", table =>
        {
            table.HasCheckConstraint("ck_favorites_id_uuid_v7", UuidV7Constraint.Sql);
            table.HasCheckConstraint("ck_favorites_catalog_type", CatalogTypeConstraint.Sql);
        });
        builder.HasKey(favorite => favorite.Id);
        builder.Property(favorite => favorite.CatalogType).HasMaxLength(16);
        builder.HasIndex(favorite => new
        {
            favorite.UserId,
            favorite.CatalogType,
            favorite.CatalogUuid
        }).IsUnique();
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(favorite => favorite.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal static class CatalogTypeConstraint
{
    public const string Sql =
        "catalog_type IN ('artist', 'show', 'source', 'source_track', 'song', 'tour', 'venue')";
}
