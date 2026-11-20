using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistDurations : Migration
    {
        // Account deletion locks the user table before cascading to these tables.
        private const string LockAgainstAccountDeletion =
            "LOCK TABLE identity.users IN SHARE MODE;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockAgainstAccountDeletion);
            migrationBuilder.AddColumn<long>(
                name: "known_duration_seconds",
                schema: "user_data",
                table: "playlists",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<int>(
                name: "tracks_with_duration",
                schema: "user_data",
                table: "playlists",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<int>(
                name: "track_duration_seconds",
                schema: "user_data",
                table: "playlist_entries",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "playlist_duration_seconds",
                schema: "user_data",
                table: "library_changes",
                type: "bigint",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockAgainstAccountDeletion);
            migrationBuilder.DropColumn(
                name: "known_duration_seconds",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(
                name: "tracks_with_duration",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(
                name: "track_duration_seconds",
                schema: "user_data",
                table: "playlist_entries"
            );

            migrationBuilder.DropColumn(
                name: "playlist_duration_seconds",
                schema: "user_data",
                table: "library_changes"
            );
        }
    }
}
