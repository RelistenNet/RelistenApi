using SimpleMigrations;

namespace Migrations;

[Migration(24, "Record the playlist a track play came from")]
public sealed class PlaylistPlays : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("024_playlist_plays.sql");
}
