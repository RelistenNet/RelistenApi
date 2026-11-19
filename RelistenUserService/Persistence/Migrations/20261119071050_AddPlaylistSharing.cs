using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_playlist_receipts_kind",
                schema: "user_data",
                table: "playlist_receipts"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_library_changes_change_type",
                schema: "user_data",
                table: "library_changes"
            );

            migrationBuilder.AddColumn<string>(
                name: "public_code",
                schema: "user_data",
                table: "playlists",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "published",
                schema: "user_data",
                table: "playlists",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<bool>(
                name: "taken_down",
                schema: "user_data",
                table: "playlists",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "followed_playlist_id",
                schema: "user_data",
                table: "library_changes",
                type: "text",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "playlist_follows",
                schema: "user_data",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_follows", x => new { x.user_id, x.playlist_id });
                    table.CheckConstraint(
                        "ck_playlist_follows_playlist_id",
                        "playlist_id ~ '^playlist_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
                    );
                    table.ForeignKey(
                        name: "FK_playlist_follows_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "playlist_reports",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    closed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_reports", x => x.id);
                    table.CheckConstraint(
                        "ck_playlist_reports_id",
                        "id ~ '^playlist_report_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
                    );
                    table.CheckConstraint(
                        "ck_playlist_reports_reason",
                        "char_length(reason) BETWEEN 1 AND 300"
                    );
                    table.ForeignKey(
                        name: "FK_playlist_reports_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalSchema: "user_data",
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_playlist_reports_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlists_public_code",
                schema: "user_data",
                table: "playlists",
                column: "public_code",
                unique: true
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_playlists_publication",
                schema: "user_data",
                table: "playlists",
                sql: "(NOT published OR public_code IS NOT NULL) AND (public_code IS NULL OR public_code ~ '^[0-9a-f]{32}$')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_playlist_receipts_kind",
                schema: "user_data",
                table: "playlist_receipts",
                sql: "kind IN ('create', 'archive', 'copy', 'publication', 'operation')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_library_changes_change_type",
                schema: "user_data",
                table: "library_changes",
                sql: "(change_type IN ('favorite_added', 'favorite_removed') AND favorite_id IS NOT NULL\n    AND catalog_type IS NOT NULL AND catalog_id IS NOT NULL AND playlist_json IS NULL AND followed_playlist_id IS NULL)\nOR (change_type = 'playlist_summary' AND favorite_id IS NULL\n    AND catalog_type IS NULL AND catalog_id IS NULL AND playlist_json IS NOT NULL AND followed_playlist_id IS NULL)\nOR (change_type IN ('playlist_followed', 'playlist_unfollowed') AND favorite_id IS NULL\n    AND catalog_type IS NULL AND catalog_id IS NULL AND playlist_json IS NULL AND followed_playlist_id IS NOT NULL)"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_library_changes_followed_playlist_id",
                schema: "user_data",
                table: "library_changes",
                sql: "followed_playlist_id ~ '^playlist_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_follows_playlist_id",
                schema: "user_data",
                table: "playlist_follows",
                column: "playlist_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_reports_playlist_id",
                schema: "user_data",
                table: "playlist_reports",
                column: "playlist_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_reports_user_id_created_at",
                schema: "user_data",
                table: "playlist_reports",
                columns: new[] { "user_id", "created_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_reports_user_id_playlist_id",
                schema: "user_data",
                table: "playlist_reports",
                columns: new[] { "user_id", "playlist_id" },
                unique: true,
                filter: "closed_at IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "playlist_follows", schema: "user_data");

            migrationBuilder.DropTable(name: "playlist_reports", schema: "user_data");

            migrationBuilder.DropIndex(
                name: "IX_playlists_public_code",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_playlists_publication",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_playlist_receipts_kind",
                schema: "user_data",
                table: "playlist_receipts"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_library_changes_change_type",
                schema: "user_data",
                table: "library_changes"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_library_changes_followed_playlist_id",
                schema: "user_data",
                table: "library_changes"
            );

            migrationBuilder.DropColumn(
                name: "public_code",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(name: "published", schema: "user_data", table: "playlists");

            migrationBuilder.DropColumn(
                name: "taken_down",
                schema: "user_data",
                table: "playlists"
            );

            migrationBuilder.DropColumn(
                name: "followed_playlist_id",
                schema: "user_data",
                table: "library_changes"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_playlist_receipts_kind",
                schema: "user_data",
                table: "playlist_receipts",
                sql: "kind IN ('create', 'archive', 'operation')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_library_changes_change_type",
                schema: "user_data",
                table: "library_changes",
                sql: "(change_type IN ('favorite_added', 'favorite_removed') AND favorite_id IS NOT NULL AND catalog_type IS NOT NULL AND catalog_id IS NOT NULL AND playlist_json IS NULL) OR (change_type = 'playlist_summary' AND favorite_id IS NULL AND catalog_type IS NULL AND catalog_id IS NULL AND playlist_json IS NOT NULL)"
            );
        }
    }
}
