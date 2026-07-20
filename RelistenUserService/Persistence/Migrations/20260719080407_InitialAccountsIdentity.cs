using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAccountsIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_applications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_applications", x => x.id);
                    table.CheckConstraint("ck_openiddict_applications_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                });

            migrationBuilder.CreateTable(
                name: "openiddict_scopes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_scopes", x => x.id);
                    table.CheckConstraint("ck_openiddict_scopes_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    username = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    username_version = table.Column<long>(type: "bigint", nullable: false),
                    username_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    username_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    security_version = table.Column<int>(type: "integer", nullable: false),
                    lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.CheckConstraint("ck_users_status", "status IN ('active', 'disabled', 'deleting')");
                    table.CheckConstraint("ck_users_username", "username = lower(username) AND username ~ '^[a-z0-9_]{3,30}$'");
                });

            migrationBuilder.CreateTable(
                name: "openiddict_authorizations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_authorizations", x => x.id);
                    table.CheckConstraint("ck_openiddict_authorizations_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_openiddict_authorizations_openiddict_applications_applicati~",
                        column: x => x.application_id,
                        principalSchema: "identity",
                        principalTable: "openiddict_applications",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "external_identities",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    provider_subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    email_at_provider = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    email_verified_at_provider = table.Column<bool>(type: "boolean", nullable: true),
                    email_is_private_relay = table.Column<bool>(type: "boolean", nullable: true),
                    email_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identities", x => x.id);
                    table.CheckConstraint("ck_external_identities_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_external_identities_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "username_command_receipts",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_username_version = table.Column<long>(type: "bigint", nullable: false),
                    payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    stored_result = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_username_command_receipts", x => x.id);
                    table.CheckConstraint("ck_username_command_receipts_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_username_command_receipts_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "username_holds",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    release_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_username_holds", x => x.id);
                    table.CheckConstraint("ck_username_holds_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_username_holds_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "native_sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    device_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    security_version = table.Column<int>(type: "integer", nullable: false),
                    authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_sessions", x => x.id);
                    table.CheckConstraint("ck_native_sessions_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_native_sessions_openiddict_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalSchema: "identity",
                        principalTable: "openiddict_authorizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_native_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_tokens", x => x.id);
                    table.CheckConstraint("ck_openiddict_tokens_id_uuid_v7", "uuid_extract_version(id) IS NOT DISTINCT FROM 7");
                    table.ForeignKey(
                        name: "FK_openiddict_tokens_openiddict_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "identity",
                        principalTable: "openiddict_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_openiddict_tokens_openiddict_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalSchema: "identity",
                        principalTable: "openiddict_authorizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_identities_issuer_provider_subject",
                schema: "identity",
                table: "external_identities",
                columns: new[] { "issuer", "provider_subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identities_user_id_issuer",
                schema: "identity",
                table: "external_identities",
                columns: new[] { "user_id", "issuer" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_native_sessions_authorization_id",
                schema: "identity",
                table: "native_sessions",
                column: "authorization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_native_sessions_user_id_revoked_at_absolute_expires_at",
                schema: "identity",
                table: "native_sessions",
                columns: new[] { "user_id", "revoked_at", "absolute_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_applications_client_id",
                schema: "identity",
                table: "openiddict_applications",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_authorizations_application_id_status_subject_type",
                schema: "identity",
                table: "openiddict_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_scopes_name",
                schema: "identity",
                table: "openiddict_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_tokens_application_id_status_subject_type",
                schema: "identity",
                table: "openiddict_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_tokens_authorization_id",
                schema: "identity",
                table: "openiddict_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_tokens_reference_id",
                schema: "identity",
                table: "openiddict_tokens",
                column: "reference_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_username_command_receipts_user_id_created_at",
                schema: "identity",
                table: "username_command_receipts",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_username_holds_release_at",
                schema: "identity",
                table: "username_holds",
                column: "release_at");

            migrationBuilder.CreateIndex(
                name: "IX_username_holds_user_id",
                schema: "identity",
                table: "username_holds",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_username_holds_username",
                schema: "identity",
                table: "username_holds",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_status_updated_at",
                schema: "identity",
                table: "users",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                schema: "identity",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "external_identities",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "native_sessions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "openiddict_scopes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "openiddict_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "username_command_receipts",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "username_holds",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "openiddict_authorizations",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "openiddict_applications",
                schema: "identity");
        }
    }
}
