using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(8, "Create playback history tables for Relisten user API")]
public sealed class CreatePlaybackHistoryTables : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.PlaybackHistoryTables);
        Execute(UserDataSchemaSql.PlaybackHistoryUserPlayedAtIndex);
        Execute(UserDataSchemaSql.PlaybackHistoryPlaylistIndex);
        Execute(UserDataSchemaSql.PlaybackHistoryClientEventIndex);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Playback history tables are not reversible in automated migrations.");
    }
}
