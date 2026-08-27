using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableBrowserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    validator_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    security_version = table.Column<int>(type: "integer", nullable: false),
                    authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sliding_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auth_sso_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    web_origin = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    capabilities = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.UniqueConstraint("AK_sessions_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_sessions_auth_sso_shape", "purpose <> 'auth_sso'\nOR (\n    auth_sso_session_id IS NULL\n    AND web_origin IS NULL\n    AND capabilities = 0\n    AND sliding_expires_at = absolute_expires_at\n)");
                    table.CheckConstraint("ck_sessions_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.CheckConstraint("ck_sessions_purpose", "purpose IN ('auth_sso', 'web')");
                    table.CheckConstraint("ck_sessions_security_version", "security_version > 0");
                    table.CheckConstraint("ck_sessions_timestamps", "authenticated_at <= created_at\nAND created_at <= last_seen_at\nAND last_seen_at <= updated_at\nAND created_at < sliding_expires_at\nAND sliding_expires_at <= absolute_expires_at\nAND (\n    revoked_at IS NULL\n    OR (\n        created_at <= revoked_at\n        AND revoked_at <= updated_at\n    )\n)");
                    table.CheckConstraint("ck_sessions_validator_hash", "octet_length(validator_hash) = 32");
                    table.CheckConstraint("ck_sessions_web_shape", "purpose <> 'web'\nOR (\n    auth_sso_session_id IS NOT NULL\n    AND web_origin IS NOT NULL\n    AND web_origin IN (\n        'https://relisten.net',\n        'https://web.relisten.localhost:5173'\n    )\n    AND capabilities = 7\n)");
                    table.ForeignKey(
                        name: "FK_sessions_sessions_auth_sso_session_id_user_id",
                        columns: x => new { x.auth_sso_session_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "sessions",
                        principalColumns: new[] { "id", "user_id" });
                    table.ForeignKey(
                        name: "FK_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_auth_sso_session_id_revoked_at",
                schema: "identity",
                table: "sessions",
                columns: new[] { "auth_sso_session_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_auth_sso_session_id_user_id",
                schema: "identity",
                table: "sessions",
                columns: new[] { "auth_sso_session_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id_revoked_at_absolute_expires_at",
                schema: "identity",
                table: "sessions",
                columns: new[] { "user_id", "revoked_at", "absolute_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sessions",
                schema: "identity");
        }
    }
}
