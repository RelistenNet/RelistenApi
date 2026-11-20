using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropUserDataEnumChecks : Migration
    {
        // Account deletion writes to identity.users before cascading into these tables.
        // Wait for it at the parent table before taking exclusive locks on the children.
        private const string LockAgainstAccountDeletion =
            "LOCK TABLE identity.users IN SHARE MODE;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockAgainstAccountDeletion);
            ReplacePlaylistRevisionCheck(migrationBuilder, "revision > 1 AND change_count > 0");

            migrationBuilder.DropCheckConstraint(
                name: "ck_playlist_receipts_kind",
                schema: "user_data",
                table: "playlist_receipts"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_library_changes_catalog_type",
                schema: "user_data",
                table: "library_changes"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_favorites_catalog_type",
                schema: "user_data",
                table: "favorites"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_favorite_mutation_receipts_catalog_type",
                schema: "user_data",
                table: "favorite_mutation_receipts"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_favorite_mutation_receipts_desired_state",
                schema: "user_data",
                table: "favorite_mutation_receipts"
            );

            // Play history's hypertable is managed by SQL migrations, outside the EF model.
            migrationBuilder.DropCheckConstraint(
                name: "ck_plays_origin",
                schema: "user_data",
                table: "plays"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockAgainstAccountDeletion);
            ReplacePlaylistRevisionCheck(
                migrationBuilder,
                "revision > 1 AND change_count > 0 AND kind IN ('content', 'rebalance')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_playlist_receipts_kind",
                schema: "user_data",
                table: "playlist_receipts",
                sql: "kind IN ('create', 'archive', 'copy', 'operation')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_library_changes_catalog_type",
                schema: "user_data",
                table: "library_changes",
                sql: "catalog_type IN ('artist', 'show', 'source', 'track', 'imported_song', 'imported_venue', 'song', 'tour', 'venue')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_favorites_catalog_type",
                schema: "user_data",
                table: "favorites",
                sql: "catalog_type IN ('artist', 'show', 'source', 'track', 'imported_song', 'imported_venue', 'song', 'tour', 'venue')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_favorite_mutation_receipts_catalog_type",
                schema: "user_data",
                table: "favorite_mutation_receipts",
                sql: "catalog_type IN ('artist', 'show', 'source', 'track', 'imported_song', 'imported_venue', 'song', 'tour', 'venue')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_favorite_mutation_receipts_desired_state",
                schema: "user_data",
                table: "favorite_mutation_receipts",
                sql: "desired_state IN ('favorite', 'not_favorite')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_plays_origin",
                schema: "user_data",
                table: "plays",
                sql: "origin IN (1, 2)"
            );
        }

        private static void ReplacePlaylistRevisionCheck(
            MigrationBuilder migrationBuilder,
            string sql
        )
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_playlist_revisions_state",
                schema: "user_data",
                table: "playlist_revisions"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_playlist_revisions_state",
                schema: "user_data",
                table: "playlist_revisions",
                sql: sql
            );
        }
    }
}
