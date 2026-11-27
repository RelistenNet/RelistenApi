using SimpleMigrations;

namespace Migrations;

[Migration(28, "Store nightly playlist popularity")]
public sealed class PlaylistPopularity : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("028_playlist_popularity.sql");
}
