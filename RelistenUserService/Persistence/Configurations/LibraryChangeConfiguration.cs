using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class LibraryChangeConfiguration : IEntityTypeConfiguration<LibraryChange>
{
    public void Configure(EntityTypeBuilder<LibraryChange> builder)
    {
        builder.ToTable(
            "library_changes",
            "user_data",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_library_changes_id_typed_id",
                    AccountIdConstraint.Sql("library_change")
                );
                table.HasCheckConstraint(
                    "ck_library_changes_favorite_id_typed_id",
                    AccountIdConstraint.Sql("favorite", "favorite_id")
                );
                table.HasCheckConstraint(
                    "ck_library_changes_catalog_type",
                    CatalogTypeConstraint.Sql
                );
                table.HasCheckConstraint(
                    "ck_library_changes_change_type",
                    "(change_type IN ('favorite_added', 'favorite_removed') AND favorite_id IS NOT NULL AND catalog_type IS NOT NULL AND catalog_id IS NOT NULL AND playlist_json IS NULL) OR (change_type = 'playlist_summary' AND favorite_id IS NULL AND catalog_type IS NULL AND catalog_id IS NULL AND playlist_json IS NOT NULL)"
                );
                table.HasCheckConstraint("ck_library_changes_revision", "revision > 0");
            }
        );
        builder.HasKey(change => change.Id);
        builder.Property(change => change.PlaylistJson).HasColumnType("jsonb");
        builder
            .Property(change => change.Id)
            .HasValueGenerator<AccountIdValueGenerator<LibraryChangeId>>()
            .ValueGeneratedOnAdd();
        builder.Property(change => change.CatalogType).HasMaxLength(16);
        builder.Property(change => change.ChangeType).HasMaxLength(24);
        builder.HasIndex(change => new { change.UserId, change.Revision }).IsUnique();
        builder.HasIndex(change => new { change.UserId, change.ChangedAt });
        builder.HasIndex(change => change.FavoriteId);
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(change => change.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
