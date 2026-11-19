using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistGuideContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_playlist_entries_track",
                schema: "user_data",
                table: "playlist_entries"
            );

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "user_data",
                table: "playlist_segments",
                type: "text",
                nullable: true
            );

            migrationBuilder.AlterColumn<string>(
                name: "track_id",
                schema: "user_data",
                table: "playlist_entries",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "user_data",
                table: "playlist_entries",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_playlist_entries_track",
                schema: "user_data",
                table: "playlist_entries",
                sql: "CASE WHEN track_id IS NULL THEN description IS NOT NULL AND description ~ '[^[:space:]]' ELSE track_id ~ '^track_[0-7][0-9a-hjkmnp-tv-z]{25}$' END"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Text blocks have no track and stored revisions carry descriptions, so neither fits
            // the earlier schema. Guide writes have no off switch: fix forward instead.
            throw new NotSupportedException(
                "Playlist guide content and its revisions must be preserved. Roll forward; see docs/api/playlist-sharing.md#rollout-and-recovery."
            );
        }
    }
}
