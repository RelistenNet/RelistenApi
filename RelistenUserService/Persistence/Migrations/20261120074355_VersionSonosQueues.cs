using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VersionSonosQueues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "queue_version",
                schema: "user_data",
                table: "sonos_queues",
                type: "bigint",
                nullable: false,
                defaultValue: 1L
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refresh_after",
                schema: "user_data",
                table: "sonos_queues",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "refreshed_queue_version",
                schema: "user_data",
                table: "sonos_queues",
                type: "bigint",
                nullable: false,
                defaultValue: 1L
            );

            migrationBuilder.AddColumn<string>(
                name: "removed_items_json",
                schema: "user_data",
                table: "sonos_queues",
                type: "text",
                nullable: false,
                defaultValue: "[]"
            );

            migrationBuilder.AddColumn<long>(
                name: "queue_version",
                schema: "user_data",
                table: "sonos_commands",
                type: "bigint",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "queue_version",
                schema: "user_data",
                table: "sonos_queues"
            );

            migrationBuilder.DropColumn(
                name: "refresh_after",
                schema: "user_data",
                table: "sonos_queues"
            );

            migrationBuilder.DropColumn(
                name: "refreshed_queue_version",
                schema: "user_data",
                table: "sonos_queues"
            );

            migrationBuilder.DropColumn(
                name: "removed_items_json",
                schema: "user_data",
                table: "sonos_queues"
            );

            migrationBuilder.DropColumn(
                name: "queue_version",
                schema: "user_data",
                table: "sonos_commands"
            );
        }
    }
}
