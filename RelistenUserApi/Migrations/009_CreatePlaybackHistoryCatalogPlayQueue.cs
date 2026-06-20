using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(9, "Create playback history catalog play queue for Relisten user API")]
public sealed class CreatePlaybackHistoryCatalogPlayQueue : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.PlaybackHistoryCatalogPlayQueue);
        Execute(UserDataSchemaSql.PlaybackHistoryCatalogPlayQueueUnprocessedIndex);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Playback history catalog play queue is not reversible in automated migrations.");
    }
}
