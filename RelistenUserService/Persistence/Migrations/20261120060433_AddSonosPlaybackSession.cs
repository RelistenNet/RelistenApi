using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSonosPlaybackSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "group_name",
                schema: "user_data",
                table: "sonos_queues",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "sonos_session_id",
                schema: "user_data",
                table: "sonos_queues",
                type: "text",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "group_name",
                schema: "user_data",
                table: "sonos_queues"
            );

            migrationBuilder.DropColumn(
                name: "sonos_session_id",
                schema: "user_data",
                table: "sonos_queues"
            );
        }
    }
}
