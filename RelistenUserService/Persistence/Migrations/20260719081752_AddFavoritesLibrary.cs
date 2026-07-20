using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFavoritesLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "user_data");

            migrationBuilder.CreateTable(
                name: "favorite_mutation_receipts",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    catalog_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    submitted_favorite_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    changed = table.Column<bool>(type: "boolean", nullable: false),
                    canonical_favorite_id = table.Column<Guid>(type: "uuid", nullable: true),
                    library_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorite_mutation_receipts", x => x.id);
                    table.CheckConstraint("ck_favorite_mutation_receipts_canonical_favorite_id", "canonical_favorite_id IS NULL OR uuid_extract_version(canonical_favorite_id) = 7");
                    table.CheckConstraint("ck_favorite_mutation_receipts_catalog_type", "catalog_type IN ('artist', 'show', 'source', 'source_track', 'song', 'tour', 'venue')");
                    table.CheckConstraint("ck_favorite_mutation_receipts_desired_state", "desired_state IN ('favorite', 'not_favorite')");
                    table.CheckConstraint("ck_favorite_mutation_receipts_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.CheckConstraint("ck_favorite_mutation_receipts_payload", "octet_length(payload_hash) = 32 AND ((desired_state = 'favorite' AND submitted_favorite_id IS NOT NULL) OR (desired_state = 'not_favorite' AND submitted_favorite_id IS NULL)) AND (desired_state <> 'favorite' OR canonical_favorite_id IS NOT NULL) AND library_revision >= 0");
                    table.CheckConstraint("ck_favorite_mutation_receipts_submitted_favorite_id", "submitted_favorite_id IS NULL OR uuid_extract_version(submitted_favorite_id) = 7");
                    table.ForeignKey(
                        name: "FK_favorite_mutation_receipts_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "favorites",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    catalog_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorites", x => x.id);
                    table.CheckConstraint("ck_favorites_catalog_type", "catalog_type IN ('artist', 'show', 'source', 'source_track', 'song', 'tour', 'venue')");
                    table.CheckConstraint("ck_favorites_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_favorites_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "library_changes",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    change_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    favorite_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    catalog_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_changes", x => x.id);
                    table.CheckConstraint("ck_library_changes_catalog_type", "catalog_type IN ('artist', 'show', 'source', 'source_track', 'song', 'tour', 'venue')");
                    table.CheckConstraint("ck_library_changes_change_type", "change_type IN ('favorite_added', 'favorite_removed')");
                    table.CheckConstraint("ck_library_changes_favorite_id_uuid_v7", "uuid_extract_version(favorite_id) IS NOT DISTINCT FROM 7");
                    table.CheckConstraint("ck_library_changes_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.CheckConstraint("ck_library_changes_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_library_changes_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "library_states",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_states", x => x.id);
                    table.CheckConstraint("ck_library_states_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.CheckConstraint("ck_library_states_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_library_states_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_mutation_receipts_submitted_favorite_id",
                schema: "user_data",
                table: "favorite_mutation_receipts",
                column: "submitted_favorite_id");

            migrationBuilder.CreateIndex(
                name: "IX_favorite_mutation_receipts_user_id_created_at",
                schema: "user_data",
                table: "favorite_mutation_receipts",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_favorites_user_id_catalog_type_catalog_uuid",
                schema: "user_data",
                table: "favorites",
                columns: new[] { "user_id", "catalog_type", "catalog_uuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_changes_favorite_id",
                schema: "user_data",
                table: "library_changes",
                column: "favorite_id");

            migrationBuilder.CreateIndex(
                name: "IX_library_changes_user_id_changed_at",
                schema: "user_data",
                table: "library_changes",
                columns: new[] { "user_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_library_changes_user_id_revision",
                schema: "user_data",
                table: "library_changes",
                columns: new[] { "user_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_states_user_id",
                schema: "user_data",
                table: "library_states",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "favorite_mutation_receipts",
                schema: "user_data");

            migrationBuilder.DropTable(
                name: "favorites",
                schema: "user_data");

            migrationBuilder.DropTable(
                name: "library_changes",
                schema: "user_data");

            migrationBuilder.DropTable(
                name: "library_states",
                schema: "user_data");
        }
    }
}
