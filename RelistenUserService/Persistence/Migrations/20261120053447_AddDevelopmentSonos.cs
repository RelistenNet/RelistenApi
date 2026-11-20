using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentSonos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sonos_grants",
                schema: "user_data",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    protected_token = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sonos_grants", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_sonos_grants_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "sonos_oauth_states",
                schema: "user_data",
                columns: table => new
                {
                    hash = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sonos_oauth_states", x => x.hash);
                    table.ForeignKey(
                        name: "FK_sonos_oauth_states_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "sonos_queues",
                schema: "user_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    household_id = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    credential_hash = table.Column<string>(type: "text", nullable: false),
                    items_json = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    status = table.Column<string>(type: "text", nullable: false),
                    playback_event_sequence = table.Column<long>(type: "bigint", nullable: true),
                    playback_event_state = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sonos_queues", x => x.id);
                    table.ForeignKey(
                        name: "FK_sonos_queues_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_sonos_oauth_states_expires_at",
                schema: "user_data",
                table: "sonos_oauth_states",
                column: "expires_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_sonos_oauth_states_user_id",
                schema: "user_data",
                table: "sonos_oauth_states",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_sonos_queues_group_id",
                schema: "user_data",
                table: "sonos_queues",
                column: "group_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_sonos_queues_user_id",
                schema: "user_data",
                table: "sonos_queues",
                column: "user_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "sonos_grants", schema: "user_data");

            migrationBuilder.DropTable(name: "sonos_oauth_states", schema: "user_data");

            migrationBuilder.DropTable(name: "sonos_queues", schema: "user_data");
        }
    }
}
