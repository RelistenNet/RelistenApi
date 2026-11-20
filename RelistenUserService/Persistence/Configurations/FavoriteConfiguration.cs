using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class FavoriteConfiguration : IEntityTypeConfiguration<Favorite>
{
    public void Configure(EntityTypeBuilder<Favorite> builder)
    {
        builder.ToTable(
            "favorites",
            "user_data",
            table =>
                table.HasCheckConstraint(
                    "ck_favorites_id_typed_id",
                    AccountIdConstraint.Sql("favorite")
                )
        );
        builder.HasKey(favorite => favorite.Id);
        builder.Property(favorite => favorite.CatalogType).HasMaxLength(16);
        builder
            .HasIndex(favorite => new
            {
                favorite.UserId,
                favorite.CatalogType,
                favorite.CatalogId,
            })
            .IsUnique();
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(favorite => favorite.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
