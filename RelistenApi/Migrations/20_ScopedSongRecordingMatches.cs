using SimpleMigrations;

namespace Migrations;

[Migration(20, "Scope song recording matches before file aggregation")]
public sealed class ScopedSongRecordingMatches : SqlFileMigration
{
    protected override void Up() =>
        Execute(Relisten.Normalization.NormalizationSql.Read("020_scoped_song_matches.sql"));
}
