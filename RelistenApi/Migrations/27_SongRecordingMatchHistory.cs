using SimpleMigrations;

namespace Migrations;

[Migration(27, "Include historical files in song popularity matches")]
public sealed class SongRecordingMatchHistory : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("027_song_recording_match_history.sql");
}
