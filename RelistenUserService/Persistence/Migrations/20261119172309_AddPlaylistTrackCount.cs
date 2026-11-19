using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistTrackCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "track_count",
                schema: "user_data",
                table: "playlists",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            // A constant default makes the new column a catalog-only change, and servers still
            // running the previous build keep inserting rows that omit it. Backfill only
            // playlists that have live tracks, from one grouped pass over their entries.
            //
            // Old servers also edit entries without updating the count, so once the rollout
            // finishes, recompute every playlist, zeroing those left with no tracks:
            //
            //   UPDATE user_data.playlists AS playlist
            //   SET track_count = (
            //       SELECT count(*) FROM user_data.playlist_entries AS entry
            //       WHERE entry.playlist_id = playlist.id
            //         AND NOT entry.deleted AND entry.track_id IS NOT NULL)
            //   WHERE track_count IS DISTINCT FROM (
            //       SELECT count(*) FROM user_data.playlist_entries AS entry
            //       WHERE entry.playlist_id = playlist.id
            //         AND NOT entry.deleted AND entry.track_id IS NOT NULL);
            migrationBuilder.Sql(
                """
                UPDATE user_data.playlists AS playlist
                SET track_count = tracks.track_count
                FROM (
                    SELECT playlist_id, count(*)::integer AS track_count
                    FROM user_data.playlist_entries
                    WHERE NOT deleted AND track_id IS NOT NULL
                    GROUP BY playlist_id
                ) AS tracks
                WHERE playlist.id = tracks.playlist_id;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "track_count",
                schema: "user_data",
                table: "playlists"
            );
        }
    }
}
