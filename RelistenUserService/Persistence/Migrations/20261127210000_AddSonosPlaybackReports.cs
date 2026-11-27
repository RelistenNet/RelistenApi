using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RelistenUserService.Persistence.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261127210000_AddSonosPlaybackReports")]
public sealed class AddSonosPlaybackReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE TABLE user_data.sonos_playback_reports (
                queue_id uuid NOT NULL REFERENCES user_data.sonos_queues(id) ON DELETE CASCADE,
                report_id uuid NOT NULL,
                PRIMARY KEY (queue_id, report_id)
            )
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE user_data.sonos_playback_reports");
}
