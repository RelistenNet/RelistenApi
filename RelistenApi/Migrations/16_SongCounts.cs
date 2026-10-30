using SimpleMigrations;

namespace Migrations;

[Migration(16, "Store canonical recording counts and imported setlist show counts")]
public sealed class SongCounts : SqlFileMigration
{
    protected override void Up() =>
        Execute(Relisten.Normalization.NormalizationSql.Read("016_song_counts.sql"));
}
