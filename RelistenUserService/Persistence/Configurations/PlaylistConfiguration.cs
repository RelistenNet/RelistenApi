using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class PlaylistConfiguration
    : IEntityTypeConfiguration<Playlist>,
        IEntityTypeConfiguration<PlaylistSegment>,
        IEntityTypeConfiguration<PlaylistEntry>,
        IEntityTypeConfiguration<PlaylistRevisionRow>,
        IEntityTypeConfiguration<PlaylistReceipt>,
        IEntityTypeConfiguration<HistoryGeneration>
{
    public void Configure(EntityTypeBuilder<Playlist> builder)
    {
        builder.ToTable(
            "playlists",
            "user_data",
            table =>
            {
                table.HasCheckConstraint("ck_playlists_id", AccountIdConstraint.Sql("playlist"));
                table.HasCheckConstraint(
                    "ck_playlists_state",
                    "revision >= 1 AND entry_count >= 0 AND char_length(name) BETWEEN 1 AND 100 AND (description IS NULL OR char_length(description) <= 1000)"
                );
            }
        );
        builder.HasKey(row => row.Id);
        builder.HasIndex(row => new
        {
            row.OwnerId,
            row.Archived,
            row.UpdatedAt,
        });
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<PlaylistSegment> builder)
    {
        builder.ToTable(
            "playlist_segments",
            "user_data",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_playlist_segments_id",
                    AccountIdConstraint.Sql("playlist_segment")
                );
                table.HasCheckConstraint(
                    "ck_playlist_segments_rank",
                    "rank IS NULL OR rank ~ '^[0-9A-Za-z]{1,48}$'"
                );
            }
        );
        builder.HasKey(row => row.Id);
        builder.HasAlternateKey(row => new { row.PlaylistId, row.Id });
        builder
            .HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(row => row.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(row => row.Rank).UseCollation("C");
        builder
            .HasIndex(row => new { row.PlaylistId, row.Rank })
            .IsUnique()
            .HasFilter("NOT deleted");
    }

    public void Configure(EntityTypeBuilder<PlaylistEntry> builder)
    {
        builder.ToTable(
            "playlist_entries",
            "user_data",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_playlist_entries_id",
                    AccountIdConstraint.Sql("playlist_entry")
                );
                table.HasCheckConstraint(
                    "ck_playlist_entries_track",
                    "CASE WHEN track_id IS NULL THEN description IS NOT NULL AND description ~ '[^[:space:]]' ELSE track_id ~ '^track_[0-7][0-9a-hjkmnp-tv-z]{25}$' END"
                );
                table.HasCheckConstraint(
                    "ck_playlist_entries_rank",
                    "rank IS NULL OR rank ~ '^[0-9A-Za-z]{1,48}$'"
                );
            }
        );
        builder.HasKey(row => row.Id);
        builder
            .HasOne<PlaylistSegment>()
            .WithMany()
            .HasForeignKey(row => new { row.PlaylistId, row.SegmentId })
            .HasPrincipalKey(row => new { row.PlaylistId, row.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(row => row.Rank).UseCollation("C");
        builder
            .HasIndex(row => new { row.SegmentId, row.Rank })
            .IsUnique()
            .HasFilter("NOT deleted");
        builder.HasIndex(row => new { row.PlaylistId, row.Deleted });
    }

    public void Configure(EntityTypeBuilder<PlaylistRevisionRow> builder)
    {
        builder.ToTable(
            "playlist_revisions",
            "user_data",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_playlist_revisions_state",
                    "revision > 1 AND change_count > 0 AND kind IN ('content', 'rebalance')"
                );
            }
        );
        builder.HasKey(row => new { row.PlaylistId, row.Revision });
        builder.Property(row => row.ChangesJson).HasColumnType("jsonb");
        builder
            .HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(row => row.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<PlaylistReceipt> builder)
    {
        builder.ToTable(
            "playlist_receipts",
            "user_data",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_playlist_receipts_id",
                    "("
                        + AccountIdConstraint.Sql("command")
                        + ") OR ("
                        + AccountIdConstraint.Sql("playlist_operation")
                        + ")"
                );
                table.HasCheckConstraint(
                    "ck_playlist_receipts_hash",
                    "octet_length(payload_hash) = 32"
                );
                table.HasCheckConstraint(
                    "ck_playlist_receipts_kind",
                    "kind IN ('create', 'archive', 'operation')"
                );
            }
        );
        builder.HasKey(row => row.Id);
        builder.HasIndex(row => row.PlaylistId);
        builder
            .HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(row => row.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.ActorId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<HistoryGeneration> builder)
    {
        builder.ToTable(
            "history_generation",
            "user_data",
            table => table.HasCheckConstraint("ck_history_generation_singleton", "id = 1")
        );
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Value).IsRequired();
    }
}
