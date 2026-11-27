using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistFollowerCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Prevent concurrent inserts or deletes between the backfill and trigger install.
            migrationBuilder.AddColumn<int>(
                name: "follower_count",
                schema: "user_data",
                table: "playlists",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<int>(
                name: "follower_count",
                schema: "user_data",
                table: "playlist_artists",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.Sql(
                "LOCK TABLE user_data.playlist_follows IN SHARE ROW EXCLUSIVE MODE"
            );
            migrationBuilder.Sql(
                """
                UPDATE user_data.playlists p SET follower_count = counts.total
                FROM (SELECT playlist_id, count(*)::integer AS total FROM user_data.playlist_follows GROUP BY playlist_id) counts
                WHERE p.id = counts.playlist_id;

                CREATE FUNCTION user_data.adjust_playlist_follower_count() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        UPDATE user_data.playlists SET follower_count = follower_count + 1 WHERE id = NEW.playlist_id;
                    ELSE
                        UPDATE user_data.playlists SET follower_count = follower_count - 1 WHERE id = OLD.playlist_id;
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE TRIGGER adjust_playlist_follower_count AFTER INSERT OR DELETE ON user_data.playlist_follows
                FOR EACH ROW EXECUTE FUNCTION user_data.adjust_playlist_follower_count();
                """
            );
            migrationBuilder.CreateIndex(
                name: "IX_playlists_follower_count_id",
                schema: "user_data",
                table: "playlists",
                columns: new[] { "follower_count", "id" },
                descending: new bool[0]
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER adjust_playlist_follower_count ON user_data.playlist_follows;
                DROP FUNCTION user_data.adjust_playlist_follower_count();
                """
            );
            migrationBuilder.DropIndex(
                name: "IX_playlists_follower_count_id",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(
                name: "follower_count",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(
                name: "follower_count",
                schema: "user_data",
                table: "playlist_artists"
            );
        }
    }
}
