using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class PlaylistSharingConfiguration
    : IEntityTypeConfiguration<PlaylistFollow>,
        IEntityTypeConfiguration<PlaylistReport>
{
    public void Configure(EntityTypeBuilder<PlaylistFollow> builder)
    {
        builder.ToTable(
            "playlist_follows",
            "user_data",
            table =>
                table.HasCheckConstraint(
                    "ck_playlist_follows_playlist_id",
                    AccountIdConstraint.Sql("playlist", "playlist_id")
                )
        );
        builder.HasKey(row => new { row.UserId, row.PlaylistId });
        builder.HasIndex(row => row.PlaylistId);
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        // Keep the follow if the source owner deletes their account.
    }

    public void Configure(EntityTypeBuilder<PlaylistReport> builder)
    {
        builder.ToTable(
            "playlist_reports",
            "user_data",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_playlist_reports_id",
                    AccountIdConstraint.Sql("playlist_report")
                );
                table.HasCheckConstraint(
                    "ck_playlist_reports_reason",
                    "char_length(reason) BETWEEN 1 AND 300"
                );
            }
        );
        builder.HasKey(row => row.Id);
        builder
            .HasIndex(row => new { row.UserId, row.PlaylistId })
            .IsUnique()
            .HasFilter("closed_at IS NULL");
        builder.HasIndex(row => new { row.UserId, row.CreatedAt });
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder
            .HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(row => row.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
