using SimpleMigrations;

namespace Migrations;

[Migration(13, "Create the media schema (audio files, features, anchors, import snapshots)")]
public sealed class MediaSchema : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("013_media.sql");
}
