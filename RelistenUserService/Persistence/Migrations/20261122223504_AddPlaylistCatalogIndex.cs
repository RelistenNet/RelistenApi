using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistCatalogIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing playlists start at indexed revision 0, below every real revision.
            // PlaylistCatalogIndexSweep rebuilds them after the first deploy: the catalog
            // join needs typed track IDs decoded, which happens in application code.
            migrationBuilder.AddColumn<int>(
                name: "catalog_duration_seconds",
                schema: "user_data",
                table: "playlists",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<long>(
                name: "catalog_indexed_revision",
                schema: "user_data",
                table: "playlists",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.CreateTable(
                name: "playlist_artists",
                schema: "user_data",
                columns: table => new
                {
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    artist_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    track_count = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_playlist_artists",
                        x => new { x.playlist_id, x.artist_uuid }
                    );
                    table.CheckConstraint("ck_playlist_artists_track_count", "track_count > 0");
                    table.ForeignKey(
                        name: "FK_playlist_artists_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalSchema: "user_data",
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_artists_artist_uuid_playlist_id",
                schema: "user_data",
                table: "playlist_artists",
                columns: new[] { "artist_uuid", "playlist_id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "playlist_artists", schema: "user_data");

            migrationBuilder.DropColumn(
                name: "catalog_duration_seconds",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(
                name: "catalog_indexed_revision",
                schema: "user_data",
                table: "playlists"
            );
        }
    }
}
