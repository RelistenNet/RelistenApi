using SimpleMigrations;

namespace Migrations;

[Migration(
    13,
    "Create the kb song catalogue: songs, aliases, evidence, track songs, runs, change log"
)]
public sealed class KbSchema : SqlFileMigration
{
    protected override void Up()
    {
        // The DDL ships with Relisten.Normalization (string-theory ddl/002_kb.sql, verbatim).
        Execute(Relisten.Normalization.NormalizationSql.Read("013_kb.sql"));

        // Bands join the kb catalogue one at a time: true once an expert has approved the
        // artist's catalogue. The per-artist API cutover and any automated re-run only touch
        // approved artists; building a catalogue by hand works for any artist.
        Execute("ALTER TABLE features ADD COLUMN kb_catalogue boolean NOT NULL DEFAULT false");
    }
}
