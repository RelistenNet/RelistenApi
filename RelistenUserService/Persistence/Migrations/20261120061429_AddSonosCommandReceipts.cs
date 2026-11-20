using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSonosCommandReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "latest_command_id",
                schema: "user_data",
                table: "sonos_queues",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "return_to_mobile",
                schema: "user_data",
                table: "sonos_oauth_states",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateTable(
                name: "sonos_commands",
                schema: "user_data",
                columns: table => new
                {
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    expected_item_id = table.Column<string>(type: "text", nullable: false),
                    target_item_id = table.Column<string>(type: "text", nullable: true),
                    target_value = table.Column<int>(type: "integer", nullable: true),
                    target_muted = table.Column<bool>(type: "boolean", nullable: true),
                    play_on_completion = table.Column<bool>(type: "boolean", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sonos_commands", x => new { x.queue_id, x.id });
                    table.ForeignKey(
                        name: "FK_sonos_commands_sonos_queues_queue_id",
                        column: x => x.queue_id,
                        principalSchema: "user_data",
                        principalTable: "sonos_queues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "sonos_commands", schema: "user_data");

            migrationBuilder.DropColumn(
                name: "latest_command_id",
                schema: "user_data",
                table: "sonos_queues"
            );

            migrationBuilder.DropColumn(
                name: "return_to_mobile",
                schema: "user_data",
                table: "sonos_oauth_states"
            );
        }
    }
}
