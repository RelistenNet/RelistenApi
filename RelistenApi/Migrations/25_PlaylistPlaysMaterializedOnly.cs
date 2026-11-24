using SimpleMigrations;

namespace Migrations;

[Migration(25, "Count playlist plays from materialized days only")]
public sealed class PlaylistPlaysMaterializedOnly : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("025_playlist_plays_materialized_only.sql");
}
