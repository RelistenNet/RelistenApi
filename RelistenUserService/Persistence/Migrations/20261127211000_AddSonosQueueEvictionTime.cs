using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RelistenUserService.Persistence.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261127211000_AddSonosQueueEvictionTime")]
public sealed class AddSonosQueueEvictionTime : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "evicted_at",
            schema: "user_data",
            table: "sonos_queues",
            type: "timestamp with time zone",
            nullable: true
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("evicted_at", "sonos_queues", "user_data");
}
