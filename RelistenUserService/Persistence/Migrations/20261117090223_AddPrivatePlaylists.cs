using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivatePlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_library_changes_change_type",
                schema: "user_data",
                table: "library_changes"
            );

            migrationBuilder.AlterColumn<string>(
                name: "favorite_id",
                schema: "user_data",
                table: "library_changes",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.AlterColumn<string>(
                name: "catalog_type",
                schema: "user_data",
                table: "library_changes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16
            );

            migrationBuilder.AlterColumn<string>(
                name: "catalog_id",
                schema: "user_data",
                table: "library_changes",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.AddColumn<string>(
                name: "playlist_json",
                schema: "user_data",
                table: "library_changes",
                type: "jsonb",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "history_generation",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_history_generation", x => x.id);
                    table.CheckConstraint("ck_history_generation_singleton", "id = 1");
                }
            );

            migrationBuilder.CreateTable(
                name: "playlists",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    entry_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlists", x => x.id);
                    table.CheckConstraint(
                        "ck_playlists_id",
                        "id ~ '^playlist_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
                    );
                    table.CheckConstraint(
                        "ck_playlists_state",
                        "revision >= 1 AND entry_count >= 0 AND char_length(name) BETWEEN 1 AND 100 AND (description IS NULL OR char_length(description) <= 1000)"
                    );
                    table.ForeignKey(
                        name: "FK_playlists_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "playlist_receipts",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: true),
                    archived = table.Column<bool>(type: "boolean", nullable: true),
                    changed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_receipts", x => x.id);
                    table.CheckConstraint(
                        "ck_playlist_receipts_hash",
                        "octet_length(payload_hash) = 32"
                    );
                    table.CheckConstraint(
                        "ck_playlist_receipts_id",
                        "(id ~ '^command_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$') OR (id ~ '^playlist_operation_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$')"
                    );
                    table.CheckConstraint(
                        "ck_playlist_receipts_kind",
                        "kind IN ('create', 'archive', 'operation')"
                    );
                    table.ForeignKey(
                        name: "FK_playlist_receipts_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalSchema: "user_data",
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_playlist_receipts_users_actor_id",
                        column: x => x.actor_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "playlist_revisions",
                schema: "user_data",
                columns: table => new
                {
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    change_count = table.Column<int>(type: "integer", nullable: false),
                    changes_json = table.Column<string>(type: "jsonb", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_playlist_revisions",
                        x => new { x.playlist_id, x.revision }
                    );
                    table.CheckConstraint(
                        "ck_playlist_revisions_state",
                        "revision > 1 AND change_count > 0 AND kind IN ('content', 'rebalance')"
                    );
                    table.ForeignKey(
                        name: "FK_playlist_revisions_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalSchema: "user_data",
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "playlist_segments",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    rank = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_segments", x => x.id);
                    table.UniqueConstraint(
                        "AK_playlist_segments_playlist_id_id",
                        x => new { x.playlist_id, x.id }
                    );
                    table.CheckConstraint(
                        "ck_playlist_segments_id",
                        "id ~ '^playlist_segment_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
                    );
                    table.CheckConstraint(
                        "ck_playlist_segments_rank",
                        "rank IS NULL OR rank ~ '^[0-9A-Za-z]{1,48}$'"
                    );
                    table.ForeignKey(
                        name: "FK_playlist_segments_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalSchema: "user_data",
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "playlist_entries",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    playlist_id = table.Column<string>(type: "text", nullable: false),
                    segment_id = table.Column<string>(type: "text", nullable: false),
                    track_id = table.Column<string>(type: "text", nullable: false),
                    rank = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_entries", x => x.id);
                    table.CheckConstraint(
                        "ck_playlist_entries_id",
                        "id ~ '^playlist_entry_[0-7][0-9a-hjkmnp-tv-z]{9}[ef][0-9a-hjkmnp-tv-z]{15}$'"
                    );
                    table.CheckConstraint(
                        "ck_playlist_entries_rank",
                        "rank IS NULL OR rank ~ '^[0-9A-Za-z]{1,48}$'"
                    );
                    table.CheckConstraint(
                        "ck_playlist_entries_track",
                        "track_id ~ '^track_[0-7][0-9a-hjkmnp-tv-z]{25}$'"
                    );
                    table.ForeignKey(
                        name: "FK_playlist_entries_playlist_segments_playlist_id_segment_id",
                        columns: x => new { x.playlist_id, x.segment_id },
                        principalSchema: "user_data",
                        principalTable: "playlist_segments",
                        principalColumns: new[] { "playlist_id", "id" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_library_changes_change_type",
                schema: "user_data",
                table: "library_changes",
                sql: "(change_type IN ('favorite_added', 'favorite_removed') AND favorite_id IS NOT NULL AND catalog_type IS NOT NULL AND catalog_id IS NOT NULL AND playlist_json IS NULL) OR (change_type = 'playlist_summary' AND favorite_id IS NULL AND catalog_type IS NULL AND catalog_id IS NULL AND playlist_json IS NOT NULL)"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_entries_playlist_id_deleted",
                schema: "user_data",
                table: "playlist_entries",
                columns: new[] { "playlist_id", "deleted" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_entries_playlist_id_segment_id",
                schema: "user_data",
                table: "playlist_entries",
                columns: new[] { "playlist_id", "segment_id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_entries_segment_id_rank",
                schema: "user_data",
                table: "playlist_entries",
                columns: new[] { "segment_id", "rank" },
                unique: true,
                filter: "NOT deleted"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_receipts_actor_id",
                schema: "user_data",
                table: "playlist_receipts",
                column: "actor_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_receipts_playlist_id",
                schema: "user_data",
                table: "playlist_receipts",
                column: "playlist_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlist_segments_playlist_id_rank",
                schema: "user_data",
                table: "playlist_segments",
                columns: new[] { "playlist_id", "rank" },
                unique: true,
                filter: "NOT deleted"
            );

            migrationBuilder.CreateIndex(
                name: "IX_playlists_owner_id_archived_updated_at",
                schema: "user_data",
                table: "playlists",
                columns: new[] { "owner_id", "archived", "updated_at" }
            );
            // A fresh generation per database, not a shared model seed or a value rotated on startup.
            migrationBuilder.InsertData(
                schema: "user_data",
                table: "history_generation",
                columns: new[] { "id", "value" },
                values: new object[] { 1, Guid.CreateVersion7().ToString() }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Private playlist rows and receipts must be preserved. Roll back to a compatible application build; do not downgrade this schema."
            );
        }
    }
}
