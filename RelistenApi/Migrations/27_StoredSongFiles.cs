using SimpleMigrations;

namespace Migrations;

[Migration(27, "Store song recording matches with each show's ranked sources")]
public sealed class StoredSongFiles : SqlFileMigration
{
    protected override void Up() =>
        Execute(Relisten.Normalization.NormalizationSql.Read("027_song_files.sql"));
}
